using System.Globalization;
using System.Text.RegularExpressions;

namespace Optimisarr.Core.Verification;

/// <summary>
/// Pure parser for the integrated-loudness summary FFmpeg's <c>ebur128</c> filter
/// prints to stderr at the end of a decode. It extracts the EBU R128 integrated
/// loudness ("I:  -23.0 LUFS"); a log without that line yields null so the caller
/// can treat "couldn't measure" distinctly from a measured value.
/// </summary>
public static partial class LoudnessParser
{
    public static double? ParseIntegratedLufs(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return null;
        }

        var match = IntegratedLoudness().Match(stderr);
        return match.Success
            && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var lufs)
            && double.IsFinite(lufs)
                ? lufs
                : null;
    }

    /// <summary>
    /// Extracts the true-peak level (dBFS, equivalent to dBTP for this purpose) from
    /// the <c>True peak</c> section the <c>ebur128</c> filter prints when run with
    /// <c>peak=true</c> ("Peak:  -1.5 dBFS"). Returns null when no peak line is present
    /// so the caller can distinguish "not measured" from a real value.
    /// </summary>
    public static double? ParseTruePeakDbtp(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return null;
        }

        var match = TruePeak().Match(stderr);
        return match.Success
            && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var dbtp)
            && double.IsFinite(dbtp)
                ? dbtp
                : null;
    }

    // Matches the integrated-loudness line, e.g. "    I:         -23.0 LUFS".
    [GeneratedRegex(@"\bI:\s*(-?\d+(?:\.\d+)?)\s*LUFS", RegexOptions.IgnoreCase)]
    private static partial Regex IntegratedLoudness();

    // Matches the true-peak line under the "True peak" section, e.g. "    Peak:  -1.5 dBFS".
    [GeneratedRegex(@"\bPeak:\s*(-?\d+(?:\.\d+)?)\s*dBFS", RegexOptions.IgnoreCase)]
    private static partial Regex TruePeak();
}
