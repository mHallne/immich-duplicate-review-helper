using ImmichDuplicateReview.Features.Batches;
using ImmichDuplicateReview.Features.Reviews;

namespace ImmichDuplicateReview.Tests.Features.Reviews;

public sealed class ReviewStoreTests : IAsyncDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"review-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task Skipped_group_remains_skipped_after_restart()
    {
        await using (var first = new ReviewStore(_databasePath))
        {
            await first.InitializeAsync();
            await first.UpsertGroupsAsync([Group("g1"), Group("g2")]);
            await first.SkipAsync("g1", "not comparable");
        }

        await using var restarted = new ReviewStore(_databasePath);
        await restarted.InitializeAsync();

        Assert.Equal(ReviewStatus.Skipped, await restarted.GetStatusAsync("g1"));
        Assert.Equal(["g2"], (await restarted.LoadPendingAsync()).Select(x => x.Id));
    }

    [Fact]
    public async Task Session_resumes_at_first_pending_group_after_restart()
    {
        await using (var first = new ReviewStore(_databasePath))
        {
            await first.InitializeAsync();
            await first.UpsertGroupsAsync([Group("g1", 1), Group("g2", 2), Group("g3", 3)]);
            await first.CreateOrResumeSessionAsync(100);
            await first.MarkReviewedAsync("g1", "{\"keep\":[\"g1-a\"],\"trash\":[\"g1-b\"]}");
            await first.SkipAsync("g2", null);
        }

        await using var restarted = new ReviewStore(_databasePath);
        await restarted.InitializeAsync();
        var session = await restarted.CreateOrResumeSessionAsync(100);
        var next = (await restarted.LoadPendingAsync()).Single();

        Assert.Equal(2, session.CurrentPosition);
        Assert.Equal("g3", next.Id);
    }

    [Fact]
    public async Task Active_batch_persists_exact_membership_and_size_after_restart()
    {
        var groups = Enumerable.Range(0, 250).Select(index => Group($"g{index:000}", index)).ToArray();
        await using (var first = new ReviewStore(_databasePath))
        {
            await first.InitializeAsync();
            await first.UpsertGroupsAsync(groups);
            var selected = CreateBatch.Handle(groups, 100);
            await first.CreateOrResumeSessionAsync(100, selected.Groups.Select(group => group.Id).ToArray());
        }

        await using var restarted = new ReviewStore(_databasePath);
        await restarted.InitializeAsync();
        var activeGroups = await restarted.LoadActiveBatchAsync();

        Assert.Equal(100, activeGroups.Count);
        Assert.Equal("g000", activeGroups[0].Id);
        Assert.Equal("g099", activeGroups[^1].Id);
        Assert.DoesNotContain(activeGroups, group => group.Id == "g100");
    }

    [Fact]
    public async Task Progress_counts_reviewed_skipped_and_remaining_within_active_batch()
    {
        await using var store = new ReviewStore(_databasePath);
        await store.InitializeAsync();
        var groups = new[] { Group("g1", 1), Group("g2", 2), Group("g3", 3), Group("outside", 4) };
        await store.UpsertGroupsAsync(groups);
        await store.CreateOrResumeSessionAsync(100, ["g1", "g2", "g3"]);
        await store.MarkReviewedAsync("g1", "{}");
        await store.SkipAsync("g2", null);

        var progress = await store.GetActiveProgressAsync();
        var next = await store.LoadNextActiveGroupAsync();

        Assert.Equal(3, progress.Total);
        Assert.Equal(1, progress.Reviewed);
        Assert.Equal(1, progress.Skipped);
        Assert.Equal(1, progress.Remaining);
        Assert.Equal("g3", next!.Id);
    }

    [Fact]
    public async Task Selected_sort_mode_survives_session_restart()
    {
        await using (var first = new ReviewStore(_databasePath))
        {
            await first.InitializeAsync();
            await first.UpsertGroupsAsync([Group("g1")]);
            await first.CreateOrResumeSessionAsync(100, ["g1"], "newest");
        }

        await using var restarted = new ReviewStore(_databasePath);
        await restarted.InitializeAsync();
        var session = await restarted.CreateOrResumeSessionAsync(100);

        Assert.Equal("newest", session.SortMode);
    }

    private static DuplicateGroup Group(string id, int day = 1) => new(id,
    [
        new($"{id}-a", "a.jpg", DateTimeOffset.UnixEpoch.AddDays(day)),
        new($"{id}-b", "b.jpg", DateTimeOffset.UnixEpoch.AddDays(day))
    ]);

    public ValueTask DisposeAsync()
    {
        if (File.Exists(_databasePath)) File.Delete(_databasePath);
        return ValueTask.CompletedTask;
    }
}
