namespace Optimisarr.Core.Rules;

/// <summary>Fail-closed structural safety rules for still-image conversion.</summary>
public static class ImageSafety
{
    public static bool RequiresKnownFrameCount(string codec) =>
        codec.Equals("gif", StringComparison.OrdinalIgnoreCase)
        || codec.Equals("webp", StringComparison.OrdinalIgnoreCase);

    public static bool MayContainMultiplePages(string codec) =>
        codec.Equals("tiff", StringComparison.OrdinalIgnoreCase);

    public static bool TargetIsLossy(string targetFormat) =>
        targetFormat.Equals("jpeg", StringComparison.OrdinalIgnoreCase)
        || targetFormat.Equals("avif", StringComparison.OrdinalIgnoreCase);

    public static bool TargetDropsStructuralData(string targetFormat) => TargetIsLossy(targetFormat);

    public static bool MayContainAlpha(string? pixelFormat)
    {
        if (string.IsNullOrWhiteSpace(pixelFormat))
        {
            return true;
        }

        var value = pixelFormat.ToLowerInvariant();
        return value is "pal8" or "ya8"
            || value.StartsWith("rgba", StringComparison.Ordinal)
            || value.StartsWith("bgra", StringComparison.Ordinal)
            || value.StartsWith("argb", StringComparison.Ordinal)
            || value.StartsWith("abgr", StringComparison.Ordinal)
            || value.StartsWith("yuva", StringComparison.Ordinal)
            || value.StartsWith("gbrap", StringComparison.Ordinal)
            || value.StartsWith("ya16", StringComparison.Ordinal);
    }

    public static bool IsHighBitDepth(string? pixelFormat, int? bitsPerRawSample)
    {
        if (bitsPerRawSample is > 8)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(pixelFormat))
        {
            return true;
        }

        var value = pixelFormat.ToLowerInvariant();
        return value.Contains("10", StringComparison.Ordinal)
            || value.Contains("12", StringComparison.Ordinal)
            || value.Contains("14", StringComparison.Ordinal)
            || value.Contains("16", StringComparison.Ordinal)
            || value.Contains("48", StringComparison.Ordinal)
            || value.Contains("64", StringComparison.Ordinal);
    }
}
