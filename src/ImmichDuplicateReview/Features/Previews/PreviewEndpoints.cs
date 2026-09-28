using ImmichDuplicateReview.Integrations.Immich;

namespace ImmichDuplicateReview.Features.Previews;

public static class PreviewEndpoints
{
    public static IEndpointRouteBuilder MapPreviewEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/assets/{assetId}/preview", async (string assetId, IImmichClient immich, CancellationToken cancellationToken) =>
        {
            var preview = await immich.GetPreviewAsync(assetId, cancellationToken);
            return Results.File(preview.Bytes, preview.ContentType, enableRangeProcessing: true);
        });
        return endpoints;
    }
}
