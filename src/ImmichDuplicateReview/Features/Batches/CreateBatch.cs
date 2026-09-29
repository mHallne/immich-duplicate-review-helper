namespace ImmichDuplicateReview.Features.Batches;

public enum SortMode
{
    Oldest,
    Newest,
    SmallestGroup,
    LargestGroup,
    LargestPotentialSaving,
    Path,
    Filename
}

public static class SortModes
{
    public static SortMode Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "oldest" => SortMode.Oldest,
        "newest" => SortMode.Newest,
        "smallest-group" => SortMode.SmallestGroup,
        "largest-group" => SortMode.LargestGroup,
        "largest-potential-saving" => SortMode.LargestPotentialSaving,
        "path" => SortMode.Path,
        "filename" => SortMode.Filename,
        _ => throw new ArgumentException($"Unsupported sort mode '{value}'.", nameof(value))
    };

    public static string ToValue(this SortMode mode) => mode switch
    {
        SortMode.Oldest => "oldest",
        SortMode.Newest => "newest",
        SortMode.SmallestGroup => "smallest-group",
        SortMode.LargestGroup => "largest-group",
        SortMode.LargestPotentialSaving => "largest-potential-saving",
        SortMode.Path => "path",
        SortMode.Filename => "filename",
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };
}

public static class CreateBatch
{
    public static ReviewBatch Handle(IEnumerable<DuplicateGroup> groups, int batchSize, SortMode sortMode = SortMode.Oldest)
    {
        if (batchSize is not (50 or 100 or 250 or 500)) throw new ArgumentOutOfRangeException(nameof(batchSize));
        var pending = groups.Where(group => group.Status == ReviewStatus.Pending);
        var ordered = sortMode switch
        {
            SortMode.Oldest => pending.OrderBy(group => group.SortDate),
            SortMode.Newest => pending.OrderByDescending(group => group.SortDate),
            SortMode.SmallestGroup => pending.OrderBy(group => group.Assets.Count),
            SortMode.LargestGroup => pending.OrderByDescending(group => group.Assets.Count),
            SortMode.LargestPotentialSaving => pending.OrderByDescending(PotentialSaving),
            SortMode.Path => pending.OrderBy(MinimumPath, StringComparer.OrdinalIgnoreCase),
            SortMode.Filename => pending.OrderBy(MinimumFilename, StringComparer.OrdinalIgnoreCase),
            _ => throw new ArgumentOutOfRangeException(nameof(sortMode))
        };
        var selected = ordered
            .ThenBy(x => x.Id, StringComparer.Ordinal)
            .Take(batchSize)
            .ToArray();
        return new(selected, batchSize);
    }

    private static long PotentialSaving(DuplicateGroup group)
    {
        var sizes = group.Assets.Select(asset => asset.FileSize ?? 0).ToArray();
        return sizes.Sum() - sizes.Max();
    }

    private static string MinimumPath(DuplicateGroup group) =>
        group.Assets.Select(asset => asset.OriginalPath ?? string.Empty).Min(StringComparer.OrdinalIgnoreCase) ?? string.Empty;

    private static string MinimumFilename(DuplicateGroup group) =>
        group.Assets.Select(asset => asset.FileName).Min(StringComparer.OrdinalIgnoreCase) ?? string.Empty;
}
