using ImmichDuplicateReview.Features.Batches;

namespace ImmichDuplicateReview.Features.Reviews;

public sealed record ReviewDecision(IReadOnlySet<string> Keep, IReadOnlySet<string> Trash, IReadOnlySet<string> Stack)
{
    public static ReviewDecision Create(DuplicateGroup group, IEnumerable<string> keep, IEnumerable<string> trash, IEnumerable<string> stack)
    {
        var decision = new ReviewDecision(keep.ToHashSet(), trash.ToHashSet(), stack.ToHashSet());
        var allIds = group.Assets.Select(x => x.Id).ToHashSet();
        if (!decision.Keep.Concat(decision.Trash).ToHashSet().SetEquals(allIds))
            throw new InvalidOperationException("Every asset must be kept or trashed.");
        if (decision.Keep.Count == 0) throw new InvalidOperationException("At least one asset must be retained.");
        if (decision.Keep.Overlaps(decision.Trash)) throw new InvalidOperationException("An asset cannot be both kept and trashed.");
        if (!decision.Stack.IsSubsetOf(decision.Keep)) throw new InvalidOperationException("Only kept assets can be stacked.");
        if (decision.Stack.Count == 1) throw new InvalidOperationException("A stack requires at least two assets.");
        return decision;
    }
}
