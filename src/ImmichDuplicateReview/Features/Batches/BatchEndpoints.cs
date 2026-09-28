using ImmichDuplicateReview.Features.Reviews;
using ImmichDuplicateReview.Integrations.Immich;

namespace ImmichDuplicateReview.Features.Batches;

public sealed record CreateBatchRequest(int BatchSize = 100);

public static class BatchEndpoints
{
    public static IEndpointRouteBuilder MapBatchEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/batches", async (
            CreateBatchRequest request,
            IImmichClient immich,
            ReviewStore store,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var groups = await immich.GetDuplicateGroupsAsync(cancellationToken);
            await store.UpsertGroupsAsync(groups, cancellationToken);
            var pending = await store.LoadPendingAsync(cancellationToken);
            var batch = CreateBatch.Handle(pending, request.BatchSize);
            var session = await store.CreateOrResumeSessionAsync(request.BatchSize, cancellationToken);
            loggerFactory.CreateLogger("Batch").LogInformation("Batch created with {GroupCount} groups and size {BatchSize}", batch.Groups.Count, request.BatchSize);
            return Results.Ok(new { session, batch.Groups });
        });
        return endpoints;
    }
}
