using ImmichDuplicateReview.Features.Reviews;
using ImmichDuplicateReview.Integrations.Immich;

namespace ImmichDuplicateReview.Features.Health;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
        endpoints.MapGet("/ready", async (ReviewStore store, IImmichClient immich, CancellationToken cancellationToken) =>
        {
            var sqliteReady = await store.IsReadyAsync(cancellationToken);
            var immichReady = await immich.IsReadyAsync(cancellationToken);
            return sqliteReady && immichReady
                ? Results.Ok(new { status = "ready" })
                : Results.Json(new { status = "not_ready", sqlite = sqliteReady, immich = immichReady }, statusCode: StatusCodes.Status503ServiceUnavailable);
        });
        return endpoints;
    }
}
