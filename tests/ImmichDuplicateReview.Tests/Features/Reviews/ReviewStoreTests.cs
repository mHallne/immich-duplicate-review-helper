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
