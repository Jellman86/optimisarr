using System.Globalization;
using System.Text.RegularExpressions;

namespace Optimisarr.Core.Verification;

/// <summary>
/// Pure parsing of ffmpeg's <c>ssim</c> filter output. The filter reports one
/// <c>All:&lt;value&gt;</c> figure per frame (a still produces one), both in the
/// per-frame stats file and in the summary line it writes to stderr. We read the
/// all-channel SSIM, taking the last occurrence so a multi-frame input collapses to
/// its final frame rather than an early one.
/// </summary>
public static partial class ImageSsimParser
{
    public static double? Parse(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        double? last = null;
        foreach (Match match in AllChannel().Matches(output))
        {
            if (double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                last = value;
            }
        }

        return last;
    }

    [GeneratedRegex(@"All:\s*([0-9]*\.?[0-9]+)")]
    private static partial Regex AllChannel();
}
