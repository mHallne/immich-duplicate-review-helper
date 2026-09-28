namespace ImmichDuplicateReview.Features.Batches;

public static class CreateBatch
{
    public static ReviewBatch Handle(IEnumerable<DuplicateGroup> groups, int batchSize)
    {
        if (batchSize is not (50 or 100 or 250 or 500)) throw new ArgumentOutOfRangeException(nameof(batchSize));
        var selected = groups
            .Where(x => x.Status == ReviewStatus.Pending)
            .OrderBy(x => x.SortDate)
            .ThenBy(x => x.Id, StringComparer.Ordinal)
            .Take(batchSize)
            .ToArray();
        return new(selected, batchSize);
    }
}
