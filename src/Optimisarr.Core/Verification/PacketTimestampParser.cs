using System.Globalization;

namespace Optimisarr.Core.Verification;

/// <summary>
/// The result of scanning a video stream's packet timestamps.
/// </summary>
/// <param name="TimestampCount">How many packets carried a readable decode timestamp.</param>
/// <param name="NonMonotonicCount">How many packets stepped backward in decode order.</param>
/// <param name="FirstRegressionDetail">A human-readable description of the first backward step, or null.</param>
/// <param name="LastPresentationSeconds">The latest presentation time seen, or null — i.e. where the video actually ends.</param>
public sealed record TimestampIntegrity(
    int TimestampCount,
    int NonMonotonicCount,
    string? FirstRegressionDetail,
    double? LastPresentationSeconds);

/// <summary>
/// Pure parser for one packet per line as <c>pts_time,dts_time</c>, as emitted by
/// <c>ffprobe -select_streams v:0 -show_entries packet=pts_time,dts_time -of csv=p=0</c>.
/// Two faults are read from the same pass:
/// <list type="bullet">
/// <item>A well-formed stream's <b>decode</b> timestamps (DTS) never go backward; a
/// backward step means the container's packets are out of order, which can stall or
/// desync playback even when the file otherwise decodes.</item>
/// <item>The latest <b>presentation</b> timestamp (PTS) is where the video genuinely
/// ends; comparing it to the source runtime reveals a truncated or partial last GOP
/// even when the output's container header still claims the full duration.</item>
/// </list>
/// Packets missing a timestamp (<c>N/A</c>) are skipped per column so a missing DTS is
/// never misread as a jump back to zero. PTS may legitimately reorder under B-frames, so
/// only DTS monotonicity is judged while the maximum PTS is tracked.
/// </summary>
public static class PacketTimestampParser
{
    public static TimestampIntegrity Parse(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
        {
            return new TimestampIntegrity(0, 0, null, null);
        }

        var dtsCount = 0;
        var regressions = 0;
        string? firstRegression = null;
        double? previousDts = null;
        double? maxPts = null;

        foreach (var line in csv.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split(',', StringSplitOptions.TrimEntries);

            if (fields.Length > 0 && TryParseSeconds(fields[0], out var pts) && (maxPts is not { } max || pts > max))
            {
                maxPts = pts;
            }

            if (fields.Length < 2 || !TryParseSeconds(fields[1], out var dts))
            {
                continue; // No decode timestamp on this packet; it carries no decode order.
            }

            dtsCount++;

            if (previousDts is { } prev && dts < prev)
            {
                regressions++;
                firstRegression ??= string.Format(
                    CultureInfo.InvariantCulture,
                    "decode timestamp went from {0:0.######}s back to {1:0.######}s",
                    prev, dts);
            }

            previousDts = dts;
        }

        return new TimestampIntegrity(dtsCount, regressions, firstRegression, maxPts);
    }

    private static bool TryParseSeconds(string field, out double value) =>
        double.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
