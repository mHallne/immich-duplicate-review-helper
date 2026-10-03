using ImmichDuplicateReview.Features.Batches;

namespace ImmichDuplicateReview.Integrations.Immich;

public sealed class PreviewContent(Stream stream, string contentType, long? contentLength = null, IDisposable? owner = null) : IAsyncDisposable
{
    public PreviewContent(byte[] bytes, string contentType) : this(new MemoryStream(bytes, writable: false), contentType, bytes.LongLength) { }

    public Stream Stream { get; } = stream;
    public string ContentType { get; } = contentType;
    public long? ContentLength { get; } = contentLength;

    public async ValueTask DisposeAsync()
    {
        await Stream.DisposeAsync();
        owner?.Dispose();
    }
}

public interface IImmichClient
{
    Task<IReadOnlyList<DuplicateGroup>> GetDuplicateGroupsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetAlbumNamesAsync(string assetId, CancellationToken cancellationToken = default);
    Task<PreviewContent> GetPreviewAsync(string assetId, CancellationToken cancellationToken = default);
    Task EnsureTrashEnabledAsync(CancellationToken cancellationToken = default);
    Task EnsureStackAsync(IReadOnlyList<string> assetIds, CancellationToken cancellationToken = default);
    Task ResolveAsync(string groupId, IReadOnlyCollection<string> keepAssetIds, IReadOnlyCollection<string> trashAssetIds, CancellationToken cancellationToken = default);
    Task<bool> IsReadyAsync(CancellationToken cancellationToken = default);
}
