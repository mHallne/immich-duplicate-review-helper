using ImmichDuplicateReview.Features.Batches;

namespace ImmichDuplicateReview.Tests.Features.Batches;

public sealed class CreateBatchTests
{
    [Fact]
    public void Selects_100_oldest_pending_groups_deterministically()
    {
        var sameDate = DateTimeOffset.Parse("2020-01-01Z");
        var groups = Enumerable.Range(0, 250).Select(i => Group($"g{i:000}", sameDate.AddDays(i))).ToList();
        groups.Add(Group("reviewed", sameDate.AddDays(-1), ReviewStatus.Reviewed));

        var batch = CreateBatch.Handle(groups, 100);

        Assert.Equal(100, batch.Groups.Count);
        Assert.Equal("g000", batch.Groups[0].Id);
        Assert.DoesNotContain(batch.Groups, x => x.Id == "reviewed");
    }

    [Fact]
    public void Equal_dates_are_ordered_by_group_id()
    {
        var date = DateTimeOffset.Parse("2020-01-01Z");
        var batch = CreateBatch.Handle([Group("b", date), Group("a", date)], 100);
        Assert.Equal(["a", "b"], batch.Groups.Select(x => x.Id));
    }

    [Theory]
    [InlineData(SortMode.Oldest, "old")]
    [InlineData(SortMode.Newest, "new")]
    [InlineData(SortMode.SmallestGroup, "new")]
    [InlineData(SortMode.LargestGroup, "large")]
    [InlineData(SortMode.LargestPotentialSaving, "new")]
    [InlineData(SortMode.Path, "new")]
    [InlineData(SortMode.Filename, "new")]
    public void Requested_sort_mode_controls_first_group_deterministically(SortMode mode, string expectedId)
    {
        var groups = new[]
        {
            DetailedGroup("old", 1, "/z", "z.jpg", [100, 50, 40]),
            DetailedGroup("new", 3, "/a", "a.jpg", [1_000, 100]),
            DetailedGroup("large", 2, "/m", "m.jpg", [10, 10, 10, 10])
        };

        var batch = CreateBatch.Handle(groups, 100, mode);

        Assert.Equal(expectedId, batch.Groups[0].Id);
    }

    private static DuplicateGroup Group(string id, DateTimeOffset date, ReviewStatus status = ReviewStatus.Pending) => new(id,
    [new($"{id}-a", "a.jpg", date), new($"{id}-b", "b.jpg", date)], status);

    private static DuplicateGroup DetailedGroup(string id, int day, string path, string filename, long[] sizes) => new(id,
        sizes.Select((size, index) => new DuplicateAsset($"{id}-{index}", index == 0 ? filename : $"{index}-{filename}", DateTimeOffset.UnixEpoch.AddDays(day), $"{path}/{index}", size)).ToArray());
}
