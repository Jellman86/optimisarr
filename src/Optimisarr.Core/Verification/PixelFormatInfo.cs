using System.Text.RegularExpressions;

namespace Optimisarr.Core.Verification;

public readonly record struct PixelFormatInfo(int BitDepth, int ChromaRank)
{
    private static readonly Regex DepthSuffix = new(
        @"(?<depth>10|12|14|16)(?:le|be)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Parses the signal fidelity represented by common FFmpeg pixel-format names.</summary>
    public static PixelFormatInfo? Parse(string? pixelFormat, int? bitsPerRawSample)
    {
        if (string.IsNullOrWhiteSpace(pixelFormat))
        {
            return null;
        }

        var value = pixelFormat.ToLowerInvariant();
        var depth = bitsPerRawSample is > 0
            ? bitsPerRawSample.Value
            : value.Contains("rgb48", StringComparison.Ordinal) || value.Contains("rgba64", StringComparison.Ordinal)
                ? 16
                : DepthSuffix.Match(value) is { Success: true } match
                    ? int.Parse(match.Groups["depth"].Value, System.Globalization.CultureInfo.InvariantCulture)
                    : 8;

        var chromaRank = value.Contains("444", StringComparison.Ordinal)
            || value.StartsWith("gbr", StringComparison.Ordinal)
            || value.StartsWith("rgb", StringComparison.Ordinal)
            || value.StartsWith("bgr", StringComparison.Ordinal)
            ? 3
            : value.Contains("422", StringComparison.Ordinal)
                ? 2
                : value.Contains("420", StringComparison.Ordinal)
                    || value.StartsWith("nv12", StringComparison.Ordinal)
                    || value.StartsWith("p010", StringComparison.Ordinal)
                    ? 1
                    : 0;

        return new PixelFormatInfo(depth, chromaRank);
    }
}
