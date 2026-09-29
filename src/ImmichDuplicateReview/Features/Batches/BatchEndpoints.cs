using ImmichDuplicateReview.Features.Reviews;
using ImmichDuplicateReview.Integrations.Immich;

namespace ImmichDuplicateReview.Features.Batches;

public sealed record CreateBatchRequest(int BatchSize = 100, string SortMode = "oldest");

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
            var sortMode = SortModes.Parse(request.SortMode);
            var batch = CreateBatch.Handle(pending, request.BatchSize, sortMode);
            var session = await store.CreateOrResumeSessionAsync(request.BatchSize, batch.Groups.Select(group => group.Id).ToArray(), sortMode.ToValue(), cancellationToken);
            var activeGroups = await store.LoadActiveBatchAsync(cancellationToken);
            var progress = await store.GetActiveProgressAsync(cancellationToken);
            loggerFactory.CreateLogger("Batch").LogInformation("Batch {SessionId} active with {GroupCount} groups and size {BatchSize}", session.Id, activeGroups.Count, session.BatchSize);
            return Results.Ok(new { session, groups = activeGroups, progress });
        });
        return endpoints;
    }
}
