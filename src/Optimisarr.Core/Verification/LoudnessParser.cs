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

        var match = LastMatch(IntegratedLoudness(), FinalSummary(stderr));
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

        var match = LastMatch(TruePeak(), FinalTruePeak(stderr));
        return match.Success
            && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var dbtp)
            && double.IsFinite(dbtp)
                ? dbtp
                : null;
    }

    // Matches the integrated-loudness line, e.g. "    I:         -23.0 LUFS".
    [GeneratedRegex(@"^\s*I:\s*(-?\d+(?:\.\d+)?)\s*LUFS\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex IntegratedLoudness();

    // Matches the true-peak line under the "True peak" section, e.g. "    Peak:  -1.5 dBFS".
    [GeneratedRegex(@"^\s*Peak:\s*(-?\d+(?:\.\d+)?)\s*dBFS\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex TruePeak();

    public static bool IsSilentTruePeak(string? log) => !string.IsNullOrWhiteSpace(log)
        && FinalSummary(log).Contains("True peak:", StringComparison.OrdinalIgnoreCase)
        && SilentPeak().IsMatch(FinalTruePeak(log));

    [GeneratedRegex(@"^\s*Peak:\s*-inf\s*dBFS\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex SilentPeak();

    private static string FinalTruePeak(string log)
    {
        var summary = FinalSummary(log);
        var start = summary.LastIndexOf("True peak:", StringComparison.OrdinalIgnoreCase);
        return start >= 0 ? summary[start..] : summary;
    }

    private static string FinalSummary(string log)
    {
        // Frame progress also contains "I: ... LUFS", initially -70. Some FFmpeg
        // builds additionally close an unused filter graph before the real one.
        // Only the last summary can describe the completed decode. A malformed
        // final summary must not borrow an earlier graph's apparently good value.
        var start = log.LastIndexOf("Summary:", StringComparison.OrdinalIgnoreCase);
        return start >= 0 ? log[start..] : log;
    }

    private static Match LastMatch(Regex pattern, string text)
    {
        var matches = pattern.Matches(text);
        return matches.Count == 0 ? Match.Empty : matches[matches.Count - 1];
    }
}
