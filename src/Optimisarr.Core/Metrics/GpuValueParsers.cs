using System.Globalization;

namespace Optimisarr.Core.Metrics;

/// <summary>
/// Parsers for the simple, unprivileged GPU-utilisation sources used as fallbacks when the
/// per-process DRM fdinfo path does not apply: NVIDIA's <c>nvidia-smi</c> query output and the
/// AMD <c>gpu_busy_percent</c> sysfs node. Both are readable without elevation.
/// </summary>
public static class GpuValueParsers
{
    /// <summary>
    /// Parses <c>nvidia-smi --query-gpu=utilization.gpu --format=csv,noheader,nounits</c>,
    /// returning the first GPU's utilisation (0–100). Tolerates a trailing "%" or "</c> %".
    /// </summary>
    public static int? ParseNvidiaSmiUtilisation(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        foreach (var raw in output.Split('\n'))
        {
            var token = raw.Trim().TrimEnd('%').Trim();
            // Some formats emit "45 %"; take the leading number.
            token = token.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                return Math.Clamp(value, 0, 100);
            }
        }

        return null;
    }

    /// <summary>
    /// Parses the integer percentage from an AMD <c>gpu_busy_percent</c> sysfs read (0–100).
    /// </summary>
    public static int? ParseSysfsBusyPercent(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        return int.TryParse(content.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, 0, 100)
            : null;
    }
}
