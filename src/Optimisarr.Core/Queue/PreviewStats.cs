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
}
