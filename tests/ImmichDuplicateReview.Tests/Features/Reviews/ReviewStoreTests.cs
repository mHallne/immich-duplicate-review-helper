using ImmichDuplicateReview.Features.Batches;
using ImmichDuplicateReview.Features.Reviews;
using Microsoft.Data.Sqlite;

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
            await first.CreateOrResumeSessionAsync(100, ["g1", "g2", "g3"]);
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

    [Fact]
    public async Task Changing_from_100_oldest_to_10_newest_replaces_the_batch_and_resets_its_cursor()
    {
        await using var store = new ReviewStore(_databasePath);
        await store.InitializeAsync();
        var groups = Enumerable.Range(1, 100).Select(index => Group($"g{index:000}", index)).ToArray();
        await store.UpsertGroupsAsync(groups);
        var oldest = await store.CreateOrResumeSessionAsync(100, groups.Select(group => group.Id).ToArray(), "oldest");
        await store.MoveActiveCursorAsync(99);

        var newestIds = groups.Reverse().Take(10).Select(group => group.Id).ToArray();
        var newest = await store.CreateOrResumeSessionAsync(10, newestIds, "newest");

        Assert.NotEqual(oldest.Id, newest.Id);
        Assert.Equal(10, newest.BatchSize);
        Assert.Equal("newest", newest.SortMode);
        Assert.Equal(0, newest.CurrentPosition);
        Assert.Equal(newestIds, (await store.LoadActiveBatchAsync()).Select(group => group.Id));
        Assert.Equal("g100", (await store.LoadCurrentActiveGroupAsync())?.Id);
    }

    [Fact]
    public async Task Group_cursor_moves_within_batch_bounds_and_survives_restart()
    {
        await using (var first = new ReviewStore(_databasePath))
        {
            await first.InitializeAsync();
            await first.UpsertGroupsAsync([Group("g1", 1), Group("g2", 2), Group("g3", 3)]);
            await first.CreateOrResumeSessionAsync(100, ["g1", "g2", "g3"]);

            Assert.Equal("g1", (await first.LoadCurrentActiveGroupAsync())!.Id);
            Assert.Equal("g2", (await first.MoveActiveCursorAsync(1))!.Id);
            Assert.Equal("g3", (await first.MoveActiveCursorAsync(20))!.Id);
        }

        await using var restarted = new ReviewStore(_databasePath);
        await restarted.InitializeAsync();
        Assert.Equal("g3", (await restarted.LoadCurrentActiveGroupAsync())!.Id);
        Assert.Equal("g1", (await restarted.MoveActiveCursorAsync(-20))!.Id);
    }

    [Fact]
    public async Task Failed_review_preserves_decision_and_safe_error_for_retry_after_restart()
    {
        const string decision = "{\"keep\":[\"g1-a\"],\"trash\":[\"g1-b\"],\"stack\":[]}";
        await using (var first = new ReviewStore(_databasePath))
        {
            await first.InitializeAsync();
            await first.UpsertGroupsAsync([Group("g1")]);
            await first.MarkFailedAsync("g1", decision, "ImmichApiException");
        }

        await using var restarted = new ReviewStore(_databasePath);
        await restarted.InitializeAsync();

        var attempt = await restarted.LoadReviewAttemptAsync("g1");
        Assert.Equal(ReviewStatus.Failed, attempt!.Status);
        Assert.Equal(decision, attempt.DecisionJson);
        Assert.Equal("ImmichApiException", attempt.FailureType);
    }

    [Fact]
    public async Task Initialization_migrates_existing_action_checkpoint_table()
    {
        await using (var connection = new SqliteConnection($"Data Source={_databasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE review_action (group_id INTEGER PRIMARY KEY, resolve_completed_at TEXT, stack_completed_at TEXT);";
            await command.ExecuteNonQueryAsync();
        }

        await using var store = new ReviewStore(_databasePath);
        await store.InitializeAsync();
        await store.UpsertGroupsAsync([Group("g1")]);
        await store.MarkResolveStartedAsync("g1");

        Assert.True(await store.IsResolveStartedAsync("g1"));
    }

    [Fact]
    public async Task Failed_session_population_rolls_back_before_a_valid_session_is_created()
    {
        await using var store = new ReviewStore(_databasePath);
        await store.InitializeAsync();
        await store.UpsertGroupsAsync([Group("g1"), Group("g2")]);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            store.CreateOrResumeSessionAsync(100, ["g1", "missing"]));
        await store.CreateOrResumeSessionAsync(100, ["g1", "g2"]);

        Assert.Equal(["g1", "g2"], (await store.LoadActiveBatchAsync()).Select(group => group.Id));
    }

    [Fact]
    public async Task Concurrent_session_requests_create_one_complete_active_session()
    {
        await using var setup = new ReviewStore(_databasePath);
        await setup.InitializeAsync();
        await setup.UpsertGroupsAsync([Group("g1"), Group("g2")]);
        var stores = Enumerable.Range(0, 8).Select(_ => new ReviewStore(_databasePath)).ToArray();

        try
        {
            var sessions = await Task.WhenAll(stores.Select(store =>
                store.CreateOrResumeSessionAsync(100, ["g1", "g2"])));

            Assert.Single(sessions.Select(session => session.Id).Distinct());
            Assert.Equal(["g1", "g2"], (await setup.LoadActiveBatchAsync()).Select(group => group.Id));
        }
        finally
        {
            foreach (var store in stores) await store.DisposeAsync();
        }
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
