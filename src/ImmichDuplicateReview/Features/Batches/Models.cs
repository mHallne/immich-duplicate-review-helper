namespace ImmichDuplicateReview.Features.Batches;

public enum ReviewStatus { Pending, Reviewed, Skipped, Failed }

public sealed record DuplicateAsset(
    string Id,
    string FileName,
    DateTimeOffset CaptureDate,
    string? OriginalPath = null,
    long? FileSize = null,
    int? Width = null,
    int? Height = null,
    string? Format = null,
    string? Camera = null,
    bool HasExif = false,
    bool HasGps = false,
    bool IsFavorite = false,
    int? Rating = null,
    IReadOnlyList<string>? AlbumNames = null)
{
    public long? PixelCount => Width is null || Height is null ? null : (long)Width * Height;
}

public sealed class DuplicateGroup
{
    public DuplicateGroup(string id, IReadOnlyList<DuplicateAsset> assets, ReviewStatus status = ReviewStatus.Pending)
    {
        if (assets.Count < 2) throw new ArgumentException("A duplicate group requires at least two assets.", nameof(assets));
        Id = id;
        Assets = assets;
        Status = status;
    }

    public string Id { get; }
    public IReadOnlyList<DuplicateAsset> Assets { get; }
    public ReviewStatus Status { get; }
    public DateTimeOffset SortDate => Assets.Min(x => x.CaptureDate);
}

public sealed record ReviewBatch(IReadOnlyList<DuplicateGroup> Groups, int BatchSize);
