namespace Optimisarr.Core.Queue;

/// <summary>
/// A byte threshold for a candidate that can no longer pass the required size-saving gate.
/// The caller still applies the normal verification policy to every completed candidate.
/// </summary>
public static class SizeBudget
{
    public static long? MaxCandidateBytes(
        long sourceBytes,
        bool requireReduction,
        bool disposable,
        double? minimumSavingPercent = null)
    {
        if (!requireReduction || disposable || sourceBytes <= 0)
        {
            return null;
        }

        if (minimumSavingPercent is { } percent && percent > 0 && percent < 100
            && double.IsFinite(percent))
        {
            // Decimal arithmetic keeps the inclusive final-size threshold exact even for a
            // multi-terabyte original. A 10% target on 1,000 bytes allows 900, never 901.
            var maximum = (long)Math.Floor(sourceBytes * (1m - (decimal)percent / 100m));
            return Math.Min(sourceBytes - 1, maximum);
        }

        return sourceBytes - 1;
    }

    public static bool Exceeded(long candidateBytes, long maxCandidateBytes) =>
        candidateBytes > maxCandidateBytes;

    /// <summary>
    /// Inclusive final-size floor for an optional maximum compression target. Unlike the maximum
    /// candidate size, this can only be judged after the encoder has finished the whole file.
    /// </summary>
    public static long? MinCandidateBytes(
        long sourceBytes,
        bool requireReduction,
        bool disposable,
        double? maximumSavingPercent = null)
    {
        if (!requireReduction || disposable || sourceBytes <= 0
            || maximumSavingPercent is not { } percent || percent <= 0 || percent >= 100
            || !double.IsFinite(percent))
        {
            return null;
        }

        return (long)Math.Ceiling(sourceBytes * (1m - (decimal)percent / 100m));
    }

    public static bool Below(long candidateBytes, long minCandidateBytes) =>
        candidateBytes < minCandidateBytes;
}
