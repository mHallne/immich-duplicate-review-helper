namespace ImmichDuplicateReview.Features.Batches;

public sealed record BatchOptions(int DefaultSize)
{
    public static BatchOptions FromConfiguration(string? value)
    {
        var size = string.IsNullOrWhiteSpace(value) ? 100 : int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        if (size is not (10 or 20 or 50 or 100))
            throw new InvalidOperationException("DEFAULT_BATCH_SIZE must be 10, 20, 50, or 100.");
        return new(size);
    }
}
