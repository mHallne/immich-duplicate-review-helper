using ImmichDuplicateReview.Features.Batches;
using ImmichDuplicateReview.Features.Reviews;
using ImmichDuplicateReview.Integrations.Immich;

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
        var workflow = new ConfirmReview(store, immich);
        var decision = ReviewDecision.Create(group, ["a"], ["b"], []);

        await workflow.HandleAsync(group, decision);
        await workflow.HandleAsync(group, decision);

        Assert.Equal(1, immich.ResolveCalls);
        Assert.Equal(ReviewStatus.Reviewed, await store.GetStatusAsync(group.Id));
    }

    [Fact]
    public async Task Failed_Immich_operation_records_failure_and_does_not_complete_review()
    {
        var group = Group();
        await using var store = await StoreWithAsync(group);
        var immich = new FakeImmichClient { Failure = new ImmichApiException("unavailable") };
        var workflow = new ConfirmReview(store, immich);
        var decision = ReviewDecision.Create(group, ["a"], ["b"], []);

        await Assert.ThrowsAsync<ImmichApiException>(() => workflow.HandleAsync(group, decision));

        Assert.Equal(ReviewStatus.Failed, await store.GetStatusAsync(group.Id));
        Assert.NotEqual(ReviewStatus.Reviewed, await store.GetStatusAsync(group.Id));
    }

    [Fact]
    public async Task Disabled_Immich_trash_prevents_resolve_operation()
    {
        var group = Group();
        await using var store = await StoreWithAsync(group);
        var immich = new FakeImmichClient { TrashSafetyFailure = new ImmichApiException("Trash disabled") };
        var workflow = new ConfirmReview(store, immich);

        await Assert.ThrowsAsync<ImmichApiException>(() => workflow.HandleAsync(group, ReviewDecision.Create(group, ["a"], ["b"], [])));

        Assert.Equal(0, immich.ResolveCalls);
        Assert.Equal(ReviewStatus.Failed, await store.GetStatusAsync(group.Id));
    }

    private async Task<ReviewStore> StoreWithAsync(DuplicateGroup group)
    {
        var store = new ReviewStore(_databasePath);
        await store.InitializeAsync();
        await store.UpsertGroupsAsync([group]);
        return store;
    }

    private static DuplicateGroup Group() => new("g", [new("a", "a.jpg", DateTimeOffset.UnixEpoch), new("b", "b.jpg", DateTimeOffset.UnixEpoch)]);

    public ValueTask DisposeAsync()
    {
        if (File.Exists(_databasePath)) File.Delete(_databasePath);
        return ValueTask.CompletedTask;
    }

    private sealed class FakeImmichClient : IImmichClient
    {
        public int ResolveCalls { get; private set; }
        public Exception? Failure { get; init; }
        public Exception? TrashSafetyFailure { get; init; }
        public Task EnsureTrashEnabledAsync(CancellationToken cancellationToken = default) => TrashSafetyFailure is null ? Task.CompletedTask : Task.FromException(TrashSafetyFailure);
        public Task<IReadOnlyList<DuplicateGroup>> GetDuplicateGroupsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PreviewContent> GetPreviewAsync(string assetId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task ResolveAsync(string groupId, IReadOnlyCollection<string> keepAssetIds, IReadOnlyCollection<string> trashAssetIds, CancellationToken cancellationToken = default)
        {
            ResolveCalls++;
            return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
        }
    }
}
