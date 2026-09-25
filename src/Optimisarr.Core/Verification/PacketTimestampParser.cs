using System.Globalization;

namespace Optimisarr.Core.Verification;

/// <summary>
/// The result of scanning one selected media stream's packet timestamps.
/// </summary>
/// <param name="TimestampCount">How many packets carried a readable presentation or decode timestamp.</param>
/// <param name="NonMonotonicCount">How many packets stepped backward in decode order.</param>
/// <param name="FirstRegressionDetail">A human-readable description of the first backward step, or null.</param>
/// <param name="LastPresentationSeconds">The latest packet endpoint seen, or null — i.e. where the selected media timeline actually ends.</param>
public sealed record TimestampIntegrity(
    int TimestampCount,
    int NonMonotonicCount,
    string? FirstRegressionDetail,
    double? LastPresentationSeconds);

/// <summary>
/// Pure parser for one packet per line as <c>pts_time,dts_time,duration_time</c>, as emitted by
/// <c>ffprobe -select_streams V:0 -show_entries packet=pts_time,dts_time,duration_time -of csv=p=0</c>.
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
        var scan = new PacketTimestampAccumulator();
        if (string.IsNullOrWhiteSpace(csv)) return scan.Result;

        foreach (var line in csv.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            scan.AddLine(line);
        }

        return scan.Result;
    }

    internal static async Task<TimestampIntegrity> ParseAsync(TextReader reader, CancellationToken cancellationToken)
    {
        var scan = new PacketTimestampAccumulator();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            scan.AddLine(line);
        }
        return scan.Result;
    }
}

/// <summary>Keeps packet-timeline measurements while ffprobe output is read one line at a time.</summary>
internal sealed class PacketTimestampAccumulator
{
    private int _timestampCount;
    private int _regressions;
    private string? _firstRegression;
    private double? _previousDts;
    private double? _maxPresentation;

    public TimestampIntegrity Result => new(_timestampCount, _regressions, _firstRegression, _maxPresentation);

    public void AddLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        var fields = line.Split(',', StringSplitOptions.TrimEntries);
        var pts = 0d;
        var dts = 0d;
        var hasPts = fields.Length > 0 && TryParseSeconds(fields[0], out pts);
        var hasDts = fields.Length > 1 && TryParseSeconds(fields[1], out dts);

        if (hasPts || hasDts) _timestampCount++;

        if (hasPts)
        {
            var endpoint = fields.Length > 2 && TryParseSeconds(fields[2], out var duration)
                ? pts + Math.Max(duration, 0)
                : pts;
            if (_maxPresentation is not { } max || endpoint > max) _maxPresentation = endpoint;
        }

        if (!hasDts) return;

        if (_previousDts is { } previous && dts < previous)
        {
            _regressions++;
            _firstRegression ??= string.Format(CultureInfo.InvariantCulture,
                "decode timestamp went from {0:0.######}s back to {1:0.######}s", previous, dts);
        }

        _previousDts = dts;
    }

    private static bool TryParseSeconds(string field, out double value) =>
        double.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
