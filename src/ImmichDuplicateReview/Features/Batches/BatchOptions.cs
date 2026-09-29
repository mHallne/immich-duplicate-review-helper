namespace ImmichDuplicateReview.Features.Batches;

public sealed record BatchOptions(int DefaultSize)
{
    public static BatchOptions FromConfiguration(string? value)
    {
        var size = string.IsNullOrWhiteSpace(value) ? 100 : int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        if (size is not (50 or 100 or 250 or 500))
            throw new InvalidOperationException("DEFAULT_BATCH_SIZE must be 50, 100, 250, or 500.");
        return new(size);
    }
}
