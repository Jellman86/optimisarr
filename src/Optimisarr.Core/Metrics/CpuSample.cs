using System.Globalization;

namespace Optimisarr.Core.Metrics;

/// <summary>
/// A point-in-time reading of the kernel's aggregate CPU time counters, parsed from the
/// first line of <c>/proc/stat</c>. Utilisation is the change in busy time over the change
/// in total time between two samples, so it needs no elevated privileges — <c>/proc/stat</c>
/// is world-readable.
/// </summary>
public readonly record struct CpuSample(long Total, long Idle)
{
    /// <summary>
    /// Parses the aggregate <c>cpu</c> line (e.g. <c>"cpu  3357 0 4313 1362393 1947 …"</c>).
    /// Idle counts both <c>idle</c> and <c>iowait</c>; total is the sum of every column.
    /// Returns null for any line that is not the aggregate cpu line or is malformed.
    /// </summary>
    public static CpuSample? Parse(string? procStatCpuLine)
    {
        if (string.IsNullOrWhiteSpace(procStatCpuLine))
        {
            return null;
        }

        var parts = procStatCpuLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5 || parts[0] != "cpu")
        {
            return null;
        }

        long total = 0;
        long idle = 0;
        for (var i = 1; i < parts.Length; i++)
        {
            if (!long.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                return null;
            }

            total += value;
            // Columns are user, nice, system, idle, iowait, … — idle is index 4, iowait index 5.
            if (i is 4 or 5)
            {
                idle += value;
            }
        }

        return new CpuSample(total, idle);
    }

    /// <summary>
    /// Busy percentage (0–100) between an earlier and later sample. Returns 0 when the total
    /// did not advance (no elapsed time) so a stalled clock never produces a bogus spike.
    /// </summary>
    public static double Utilisation(CpuSample previous, CpuSample current)
    {
        var totalDelta = current.Total - previous.Total;
        if (totalDelta <= 0)
        {
            return 0;
        }

        var idleDelta = current.Idle - previous.Idle;
        var busy = (totalDelta - idleDelta) / (double)totalDelta * 100.0;
        return Math.Clamp(busy, 0, 100);
    }
}
