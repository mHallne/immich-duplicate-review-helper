using ImmichDuplicateReview.Integrations.Immich;

namespace ImmichDuplicateReview.Features.Reviews;

public sealed record ConfirmReviewRequest(string[] KeepAssetIds, string[] TrashAssetIds, string[] StackAssetIds);

public static class ReviewEndpoints
{
    public static IEndpointRouteBuilder MapReviewEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/review/next", async (ReviewStore store, CancellationToken cancellationToken) =>
        {
            var group = await store.LoadCurrentActiveGroupAsync(cancellationToken);
            return group is null ? Results.NoContent() : Results.Ok(group);
        });

        endpoints.MapPost("/api/review/{groupId}/skip", async (
            string groupId, ReviewStore store, ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
        {
            if (await store.LoadGroupAsync(groupId, cancellationToken) is null) return Results.NotFound();
            await store.SkipAsync(groupId, null, cancellationToken);
            loggerFactory.CreateLogger("Review").LogInformation("Review skipped for group {GroupId}", groupId);
            return Results.NoContent();
        });

        endpoints.MapPost("/api/review/{groupId}/confirm", async (
            string groupId,
            ConfirmReviewRequest request,
            ReviewStore store,
            ConfirmReview workflow,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var group = await store.LoadGroupAsync(groupId, cancellationToken);
            if (group is null) return Results.NotFound();
            var decision = ReviewDecision.Create(group, request.KeepAssetIds, request.TrashAssetIds, request.StackAssetIds);
            await workflow.HandleAsync(group, decision, cancellationToken);
            loggerFactory.CreateLogger("Review").LogInformation("Review confirmed for group {GroupId}", groupId);
            return Results.NoContent();
        });
        return endpoints;
    }
}
