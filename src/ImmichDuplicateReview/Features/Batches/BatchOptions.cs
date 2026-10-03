namespace ImmichDuplicateReview.Features.Batches;

public sealed record BatchOptions(int DefaultSize)
{
    public const int DefaultBatchSize = 100;
    public static IReadOnlyList<int> AllowedSizes { get; } = Array.AsReadOnly([10, 20, 50, 100]);

    public static bool IsAllowed(int size) => AllowedSizes.Contains(size);

    public static BatchOptions FromConfiguration(string? value)
    {
        var size = string.IsNullOrWhiteSpace(value) ? DefaultBatchSize : int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        if (!IsAllowed(size))
            throw new InvalidOperationException($"DEFAULT_BATCH_SIZE must be one of: {string.Join(", ", AllowedSizes)}.");
        return new(size);
    }
}
