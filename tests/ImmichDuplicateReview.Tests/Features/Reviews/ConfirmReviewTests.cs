using ImmichDuplicateReview.Features.Batches;
using ImmichDuplicateReview.Features.Reviews;
using ImmichDuplicateReview.Integrations.Immich;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichDuplicateReview.Tests.Features.Reviews;

public sealed class ConfirmReviewTests : IAsyncDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"confirm-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task Confirmation_calls_Immich_once_then_marks_reviewed_and_is_idempotent()
    {
        var group = Group();
        await using var store = await StoreWithAsync(group);
        var immich = new FakeImmichClient();
        var workflow = Workflow(store, immich);
        var decision = ReviewDecision.Create(group, ["a"], ["b"], []);

        await workflow.HandleAsync(group, decision);
        await workflow.HandleAsync(group, decision);

        Assert.Equal(1, immich.ResolveCalls);
        Assert.Equal(ReviewStatus.Reviewed, await store.GetStatusAsync(group.Id));
    }

    [Fact]
    public async Task Concurrent_confirmations_issue_one_resolve_operation()
    {
        var group = Group();
        await using var store = await StoreWithAsync(group);
        var immich = new FakeImmichClient { ResolveDelay = TimeSpan.FromMilliseconds(100), CurrentDuplicateGroups = [group] };
        var workflow = Workflow(store, immich);
        var decision = ReviewDecision.Create(group, ["a"], ["b"], []);

        await Task.WhenAll(workflow.HandleAsync(group, decision), workflow.HandleAsync(group, decision));

        Assert.Equal(1, immich.ResolveCalls);
        Assert.Equal(ReviewStatus.Reviewed, await store.GetStatusAsync(group.Id));
    }

    [Fact]
    public async Task Failed_Immich_operation_records_failure_and_does_not_complete_review()
    {
        var group = Group();
        await using var store = await StoreWithAsync(group);
        var immich = new FakeImmichClient { Failure = new ImmichApiException("unavailable") };
        var workflow = Workflow(store, immich);
        var decision = ReviewDecision.Create(group, ["a"], ["b"], []);

        await Assert.ThrowsAsync<ImmichApiException>(() => workflow.HandleAsync(group, decision));

        Assert.Equal(ReviewStatus.Failed, await store.GetStatusAsync(group.Id));
        Assert.NotEqual(ReviewStatus.Reviewed, await store.GetStatusAsync(group.Id));
        Assert.Equal(group.Id, Assert.Single(await store.LoadPendingAsync()).Id);
    }

    [Fact]
    public async Task Disabled_Immich_trash_prevents_resolve_operation()
    {
        var group = Group();
        await using var store = await StoreWithAsync(group);
        var immich = new FakeImmichClient { TrashSafetyFailure = new ImmichApiException("Trash disabled") };
        var workflow = Workflow(store, immich);

        await Assert.ThrowsAsync<ImmichApiException>(() => workflow.HandleAsync(group, ReviewDecision.Create(group, ["a"], ["b"], [])));

        Assert.Equal(0, immich.ResolveCalls);
        Assert.Equal(ReviewStatus.Failed, await store.GetStatusAsync(group.Id));
    }

    [Fact]
    public async Task Stack_failure_retries_only_stack_after_resolve_checkpoint()
    {
        var group = new DuplicateGroup("g",
        [
            new("a", "a.jpg", DateTimeOffset.UnixEpoch),
            new("b", "b.jpg", DateTimeOffset.UnixEpoch),
            new("c", "c.jpg", DateTimeOffset.UnixEpoch)
        ]);
        await using var store = await StoreWithAsync(group);
        var immich = new FakeImmichClient { StackFailuresRemaining = 1 };
        var workflow = Workflow(store, immich);
        var decision = ReviewDecision.Create(group, ["a", "c"], ["b"], ["a", "c"]);

        await Assert.ThrowsAsync<ImmichApiException>(() => workflow.HandleAsync(group, decision));
        await workflow.HandleAsync(group, decision);

        Assert.Equal(1, immich.ResolveCalls);
        Assert.Equal(2, immich.StackCalls);
        Assert.Equal(["resolve", "stack", "stack"], immich.Operations);
        Assert.Equal(ReviewStatus.Reviewed, await store.GetStatusAsync(group.Id));
    }

    [Fact]
    public async Task Retrying_failed_review_writes_structured_retry_log()
    {
        var group = Group();
        await using var store = await StoreWithAsync(group);
        await store.MarkFailedAsync(group.Id, "{}", "ImmichApiException");
        var logger = new CapturingLogger();
        var workflow = new ConfirmReview(store, new FakeImmichClient(), logger);

        await workflow.HandleAsync(group, ReviewDecision.Create(group, ["a"], ["b"], []));

        var entry = Assert.Single(logger.Entries, item => item.EventId.Name == "ReviewRetry");
        Assert.Contains(group.Id, entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_group_after_lost_response_requires_manual_verification_without_repeating_resolve()
    {
        var group = Group();
        await using var store = await StoreWithAsync(group);
        var immich = new FakeImmichClient { AmbiguousFailuresRemaining = 1, CurrentDuplicateGroups = [] };
        var workflow = Workflow(store, immich);
        var decision = ReviewDecision.Create(group, ["a"], ["b"], []);

        await Assert.ThrowsAsync<HttpRequestException>(() => workflow.HandleAsync(group, decision));
        var exception = await Assert.ThrowsAsync<AmbiguousResolveException>(() => workflow.HandleAsync(group, decision));

        Assert.Equal(1, immich.ResolveCalls);
        Assert.Equal(1, immich.DuplicateLookupCalls);
        Assert.Contains("manually verify", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ReviewStatus.Failed, await store.GetStatusAsync(group.Id));
        Assert.False(await store.IsResolveCompletedAsync(group.Id));
        Assert.Equal("AmbiguousResolveException", (await store.LoadReviewAttemptAsync(group.Id))?.FailureType);
    }

    [Fact]
    public async Task Unresolved_group_is_safely_retried_after_ambiguous_failure()
    {
        var group = Group();
        await using var store = await StoreWithAsync(group);
        var immich = new FakeImmichClient { AmbiguousFailuresRemaining = 1, CurrentDuplicateGroups = [group] };
        var workflow = Workflow(store, immich);
        var decision = ReviewDecision.Create(group, ["a"], ["b"], []);

        await Assert.ThrowsAsync<HttpRequestException>(() => workflow.HandleAsync(group, decision));
        await workflow.HandleAsync(group, decision);

        Assert.Equal(2, immich.ResolveCalls);
        Assert.Equal(ReviewStatus.Reviewed, await store.GetStatusAsync(group.Id));
    }

    private async Task<ReviewStore> StoreWithAsync(DuplicateGroup group)
    {
        var store = new ReviewStore(_databasePath);
        await store.InitializeAsync();
        await store.UpsertGroupsAsync([group]);
        return store;
    }

    private static ConfirmReview Workflow(ReviewStore store, IImmichClient immich) =>
        new(store, immich, NullLogger<ConfirmReview>.Instance);

    private static DuplicateGroup Group() => new("g", [new("a", "a.jpg", DateTimeOffset.UnixEpoch), new("b", "b.jpg", DateTimeOffset.UnixEpoch)]);

    public ValueTask DisposeAsync()
    {
        if (File.Exists(_databasePath)) File.Delete(_databasePath);
        return ValueTask.CompletedTask;
    }

    private sealed class FakeImmichClient : IImmichClient
    {
        public Task<IReadOnlyList<string>> GetAlbumNamesAsync(string assetId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public int ResolveCalls { get; private set; }
        public Exception? Failure { get; init; }
        public Exception? TrashSafetyFailure { get; init; }
        public int StackFailuresRemaining { get; set; }
        public int AmbiguousFailuresRemaining { get; set; }
        public TimeSpan ResolveDelay { get; init; }
        public int DuplicateLookupCalls { get; private set; }
        public IReadOnlyList<DuplicateGroup> CurrentDuplicateGroups { get; init; } = [];
        public int StackCalls { get; private set; }
        public List<string> Operations { get; } = [];
        public Task EnsureTrashEnabledAsync(CancellationToken cancellationToken = default) => TrashSafetyFailure is null ? Task.CompletedTask : Task.FromException(TrashSafetyFailure);
        public Task EnsureStackAsync(IReadOnlyList<string> assetIds, CancellationToken cancellationToken = default)
        {
            StackCalls++;
            Operations.Add("stack");
            if (StackFailuresRemaining-- > 0) return Task.FromException(new ImmichApiException("stack failed"));
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<DuplicateGroup>> GetDuplicateGroupsAsync(CancellationToken cancellationToken = default)
        {
            DuplicateLookupCalls++;
            return Task.FromResult(CurrentDuplicateGroups);
        }
        public Task<PreviewContent> GetPreviewAsync(string assetId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public async Task ResolveAsync(string groupId, IReadOnlyCollection<string> keepAssetIds, IReadOnlyCollection<string> trashAssetIds, CancellationToken cancellationToken = default)
        {
            ResolveCalls++;
            Operations.Add("resolve");
            if (ResolveDelay > TimeSpan.Zero) await Task.Delay(ResolveDelay, cancellationToken);
            if (AmbiguousFailuresRemaining-- > 0) throw new HttpRequestException("response lost");
            if (Failure is not null) throw Failure;
        }
    }

    private sealed class CapturingLogger : ILogger<ConfirmReview>
    {
        public List<(EventId EventId, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((eventId, formatter(state, exception)));
    }
}
