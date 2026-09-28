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

    private static DuplicateGroup Group(string id, DateTimeOffset date, ReviewStatus status = ReviewStatus.Pending) => new(id,
    [new($"{id}-a", "a.jpg", date), new($"{id}-b", "b.jpg", date)], status);
}
