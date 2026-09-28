using ImmichDuplicateReview.Features.Batches;

namespace ImmichDuplicateReview.Integrations.Immich;

public sealed record PreviewContent(byte[] Bytes, string ContentType);

public interface IImmichClient
{
    Task<IReadOnlyList<DuplicateGroup>> GetDuplicateGroupsAsync(CancellationToken cancellationToken = default);
    Task<PreviewContent> GetPreviewAsync(string assetId, CancellationToken cancellationToken = default);
    Task EnsureTrashEnabledAsync(CancellationToken cancellationToken = default);
    Task ResolveAsync(string groupId, IReadOnlyCollection<string> keepAssetIds, IReadOnlyCollection<string> trashAssetIds, CancellationToken cancellationToken = default);
    Task<bool> IsReadyAsync(CancellationToken cancellationToken = default);
}
