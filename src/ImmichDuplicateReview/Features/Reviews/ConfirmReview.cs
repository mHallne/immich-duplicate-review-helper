using System.Collections.Concurrent;
using System.Text.Json;
using ImmichDuplicateReview.Features.Batches;
using ImmichDuplicateReview.Integrations.Immich;

namespace ImmichDuplicateReview.Features.Reviews;

public sealed class ConfirmReview(ReviewStore store, IImmichClient immichClient, ILogger<ConfirmReview> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly EventId RetryEvent = new(1001, "ReviewRetry");
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ConfirmationLocks = new(StringComparer.Ordinal);

    public async Task HandleAsync(DuplicateGroup group, ReviewDecision decision, CancellationToken cancellationToken = default)
    {
        var confirmationLock = ConfirmationLocks.GetOrAdd(group.Id, static _ => new SemaphoreSlim(1, 1));
        await confirmationLock.WaitAsync(cancellationToken);
        try
        {
            await HandleCoreAsync(group, decision, cancellationToken);
        }
        finally
        {
            confirmationLock.Release();
        }
    }

    private async Task HandleCoreAsync(DuplicateGroup group, ReviewDecision decision, CancellationToken cancellationToken)
    {
        var status = await store.GetStatusAsync(group.Id, cancellationToken);
        if (status == ReviewStatus.Reviewed) return;
        if (status == ReviewStatus.Failed)
            logger.LogInformation(RetryEvent, "Retrying review for group {GroupId}", group.Id);

        var validated = ReviewDecision.Create(group, decision.Keep, decision.Trash, decision.Stack);
        var decisionJson = JsonSerializer.Serialize(new
        {
            keep = validated.Keep.Order(StringComparer.Ordinal),
            trash = validated.Trash.Order(StringComparer.Ordinal),
            stack = validated.Stack.Order(StringComparer.Ordinal)
        }, JsonOptions);

        try
        {
            if (!await store.IsResolveCompletedAsync(group.Id, cancellationToken))
            {
                var started = await store.IsResolveStartedAsync(group.Id, cancellationToken);
                if (started)
                {
                    var groups = await immichClient.GetDuplicateGroupsAsync(cancellationToken);
                    if (groups.All(candidate => candidate.Id != group.Id))
                    {
                        logger.LogInformation("Resolve outcome reconciled for group {GroupId}; group is no longer returned by Immich", group.Id);
                        await store.MarkResolveCompletedAsync(group.Id, cancellationToken);
                    }
                }

                if (!await store.IsResolveCompletedAsync(group.Id, cancellationToken))
                {
                    if (validated.Trash.Count > 0) await immichClient.EnsureTrashEnabledAsync(cancellationToken);
                    if (!started) await store.MarkResolveStartedAsync(group.Id, cancellationToken);
                    await immichClient.ResolveAsync(group.Id, validated.Keep, validated.Trash, cancellationToken);
                    await store.MarkResolveCompletedAsync(group.Id, cancellationToken);
                }
            }
            if (validated.Stack.Count > 0)
            {
                var orderedStackIds = group.Assets.Select(asset => asset.Id).Where(validated.Stack.Contains).ToArray();
                await immichClient.EnsureStackAsync(orderedStackIds, cancellationToken);
                await store.MarkStackCompletedAsync(group.Id, cancellationToken);
            }
            await store.MarkReviewedAsync(group.Id, decisionJson, cancellationToken);
            logger.LogInformation("Review confirmed for group {GroupId}", group.Id);
        }
        catch (Exception exception) when (exception is ImmichApiException or HttpRequestException)
        {
            await store.MarkFailedAsync(group.Id, decisionJson, exception.GetType().Name, cancellationToken);
            throw;
        }
    }
}
