using System.Collections.Concurrent;
using ImmichDuplicateReview.Integrations.Immich;

namespace ImmichDuplicateReview.Features.Batches;

public sealed class AlbumMetadataEnricher(IImmichClient immichClient, ILogger<AlbumMetadataEnricher> logger)
{
    public async Task<IReadOnlyList<DuplicateGroup>> EnrichAsync(
        IReadOnlyList<DuplicateGroup> groups,
        CancellationToken cancellationToken = default)
    {
        var assets = groups.SelectMany(group => group.Assets).Where(asset => asset.AlbumNames is null).ToArray();
        var albumNames = new ConcurrentDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        await Parallel.ForEachAsync(assets, new ParallelOptions
        {
            MaxDegreeOfParallelism = 8,
            CancellationToken = cancellationToken
        }, async (asset, token) =>
        {
            try
            {
                albumNames[asset.Id] = await immichClient.GetAlbumNamesAsync(asset.Id, token);
            }
            catch (Exception exception) when (exception is ImmichApiException or HttpRequestException)
            {
                logger.LogWarning(
                    "Album lookup failed for asset {AssetId} with {FailureType}; review will continue without album metadata",
                    asset.Id,
                    exception.GetType().Name);
            }
        });

        return groups.Select(group => new DuplicateGroup(
            group.Id,
            group.Assets.Select(asset => albumNames.TryGetValue(asset.Id, out var names) ? asset with { AlbumNames = names } : asset).ToArray(),
            group.Status)).ToArray();
    }
}
