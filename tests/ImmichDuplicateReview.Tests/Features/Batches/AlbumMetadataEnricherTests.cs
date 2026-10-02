using ImmichDuplicateReview.Features.Batches;
using ImmichDuplicateReview.Integrations.Immich;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichDuplicateReview.Tests.Features.Batches;

public sealed class AlbumMetadataEnricherTests
{
    [Fact]
    public async Task Enriches_available_album_names_and_preserves_assets_when_lookup_fails()
    {
        var group = new DuplicateGroup("g",
        [
            new("a", "a.jpg", DateTimeOffset.UnixEpoch),
            new("b", "b.jpg", DateTimeOffset.UnixEpoch)
        ]);
        var client = new AlbumClient();

        var enriched = await new AlbumMetadataEnricher(client, NullLogger<AlbumMetadataEnricher>.Instance).EnrichAsync([group]);

        Assert.Equal(["Family", "Trips"], enriched[0].Assets[0].AlbumNames);
        Assert.Null(enriched[0].Assets[1].AlbumNames);
    }

    private sealed class AlbumClient : IImmichClient
    {
        public Task<IReadOnlyList<string>> GetAlbumNamesAsync(string assetId, CancellationToken cancellationToken = default) =>
            assetId == "a" ? Task.FromResult<IReadOnlyList<string>>(["Family", "Trips"]) : Task.FromException<IReadOnlyList<string>>(new ImmichApiException("forbidden"));
        public Task<IReadOnlyList<DuplicateGroup>> GetDuplicateGroupsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PreviewContent> GetPreviewAsync(string assetId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task EnsureTrashEnabledAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task EnsureStackAsync(IReadOnlyList<string> assetIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ResolveAsync(string groupId, IReadOnlyCollection<string> keepAssetIds, IReadOnlyCollection<string> trashAssetIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
