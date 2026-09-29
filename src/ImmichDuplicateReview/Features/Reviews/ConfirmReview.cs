using System.Text.Json;
using ImmichDuplicateReview.Features.Batches;
using ImmichDuplicateReview.Integrations.Immich;

namespace ImmichDuplicateReview.Features.Reviews;

public sealed class ConfirmReview(ReviewStore store, IImmichClient immichClient)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task HandleAsync(DuplicateGroup group, ReviewDecision decision, CancellationToken cancellationToken = default)
    {
        if (await store.GetStatusAsync(group.Id, cancellationToken) == ReviewStatus.Reviewed) return;

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
                if (validated.Trash.Count > 0) await immichClient.EnsureTrashEnabledAsync(cancellationToken);
                await immichClient.ResolveAsync(group.Id, validated.Keep, validated.Trash, cancellationToken);
                await store.MarkResolveCompletedAsync(group.Id, cancellationToken);
            }
            if (validated.Stack.Count > 0)
            {
                var orderedStackIds = group.Assets.Select(asset => asset.Id).Where(validated.Stack.Contains).ToArray();
                await immichClient.EnsureStackAsync(orderedStackIds, cancellationToken);
                await store.MarkStackCompletedAsync(group.Id, cancellationToken);
            }
            await store.MarkReviewedAsync(group.Id, decisionJson, cancellationToken);
        }
        catch (Exception exception) when (exception is ImmichApiException or HttpRequestException)
        {
            await store.MarkFailedAsync(group.Id, decisionJson, exception.Message, cancellationToken);
            throw;
        }
    }
}
