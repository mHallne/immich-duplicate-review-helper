using ImmichDuplicateReview.Features.Batches;
using ImmichDuplicateReview.Features.Reviews;

namespace ImmichDuplicateReview.Tests.Features.Reviews;

public sealed class ReviewDecisionTests
{
    private static readonly DuplicateGroup Group = new("g", [new("a", "a.jpg", DateTimeOffset.UnixEpoch), new("b", "b.jpg", DateTimeOffset.UnixEpoch)]);

    [Fact]
    public void Group_with_fewer_than_two_assets_is_invalid() =>
        Assert.Throws<ArgumentException>(() => new DuplicateGroup("g", [new("a", "a.jpg", DateTimeOffset.UnixEpoch)]));

    [Fact]
    public void Trash_requires_a_retained_asset() =>
        Assert.Throws<InvalidOperationException>(() => ReviewDecision.Create(Group, [], ["a", "b"], []));

    [Fact]
    public void Stack_cannot_include_a_trashed_asset() =>
        Assert.Throws<InvalidOperationException>(() => ReviewDecision.Create(Group, ["a"], ["b"], ["a", "b"]));
}
