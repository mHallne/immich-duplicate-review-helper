using ImmichDuplicateReview.Integrations.Immich;

namespace ImmichDuplicateReview.Features.Previews;

public static class PreviewEndpoints
{
    public static IEndpointRouteBuilder MapPreviewEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/assets/{assetId}/preview", async (string assetId, IImmichClient immich, CancellationToken cancellationToken) =>
        {
            var preview = await immich.GetPreviewAsync(assetId, cancellationToken);
            return new PreviewResult(preview);
        });
        return endpoints;
    }

    private sealed class PreviewResult(PreviewContent preview) : IResult
    {
        public async Task ExecuteAsync(HttpContext context)
        {
            await using (preview)
            {
                context.Response.ContentType = preview.ContentType;
                context.Response.ContentLength = preview.ContentLength;
                await preview.Stream.CopyToAsync(context.Response.Body, context.RequestAborted);
            }
        }
    }
}
