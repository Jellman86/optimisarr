namespace Optimisarr.Core.Queue;

/// <summary>Pure comparison maths for a settings preview (original vs encoded).</summary>
public static class PreviewStats
{
    /// <summary>
    /// Space saved by the encode as a percentage of the original size (positive = smaller),
    /// rounded to one decimal. Null when either size is missing or the original is empty; a
    /// larger output yields a negative percentage rather than being hidden.
    /// </summary>
    public static double? SavingPercent(long? originalBytes, long? encodedBytes)
    {
        if (originalBytes is not > 0 || encodedBytes is null)
        {
            return null;
        }

        return Math.Round((1.0 - (double)encodedBytes.Value / originalBytes.Value) * 100.0, 1);
    }

    /// <summary>
    /// Space saving compared by *bitrate* (bytes per second) rather than raw size, so a short
    /// preview clip of a long source still reports a representative figure. Falls back to null when
    /// any input is missing or non-positive. For equal durations this equals <see cref="SavingPercent"/>.
    /// </summary>
    public static double? SavingPercentByRate(
        long? originalBytes, double? originalSeconds, long? encodedBytes, double? encodedSeconds)
    {
        if (originalBytes is not > 0 || originalSeconds is not > 0
            || encodedBytes is not > 0 || encodedSeconds is not > 0)
        {
            return null;
        }

        var originalRate = originalBytes.Value / originalSeconds.Value;
        var encodedRate = encodedBytes.Value / encodedSeconds.Value;
        return Math.Round((1.0 - encodedRate / originalRate) * 100.0, 1);
    }
}
