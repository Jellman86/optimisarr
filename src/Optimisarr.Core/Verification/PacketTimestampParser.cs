using System.Globalization;

namespace Optimisarr.Core.Verification;

/// <summary>
/// The result of scanning a video stream's decode timestamps for monotonicity.
/// </summary>
/// <param name="TimestampCount">How many packets carried a readable decode timestamp.</param>
/// <param name="NonMonotonicCount">How many packets stepped backward from the previous one.</param>
/// <param name="FirstRegressionDetail">A human-readable description of the first backward step, or null.</param>
public sealed record TimestampIntegrity(int TimestampCount, int NonMonotonicCount, string? FirstRegressionDetail);

/// <summary>
/// Pure parser for one decode timestamp (<c>dts_time</c>) per line, as emitted by
/// <c>ffprobe -select_streams v:0 -show_entries packet=dts_time -of csv=p=0</c>. A
/// well-formed stream's decode timestamps never go backward; a backward step means the
/// container's packets are out of order, which can stall or desync playback even when
/// the file otherwise decodes. Packets without a timestamp (<c>N/A</c>) are skipped so a
/// missing DTS is never misread as a jump back to zero.
/// </summary>
public static class PacketTimestampParser
{
    public static TimestampIntegrity Parse(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
        {
            return new TimestampIntegrity(0, 0, null);
        }

        var count = 0;
        var regressions = 0;
        string? firstRegression = null;
        double? previous = null;

        foreach (var line in csv.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!double.TryParse(line, NumberStyles.Float, CultureInfo.InvariantCulture, out var dts))
            {
                continue; // "N/A" or any non-numeric packet carries no usable timestamp.
            }

            count++;

            if (previous is { } prev && dts < prev)
            {
                regressions++;
                firstRegression ??= string.Format(
                    CultureInfo.InvariantCulture,
                    "decode timestamp went from {0:0.######}s back to {1:0.######}s",
                    prev, dts);
            }

            previous = dts;
        }

        return new TimestampIntegrity(count, regressions, firstRegression);
    }
}
