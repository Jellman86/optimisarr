using System.Globalization;
using System.Text.Json;

namespace Optimisarr.Sidecar.Core.Session;

/// <summary>
/// How far into its container a file's first picture sits.
///
/// <para>FFmpeg seeks and stamps frames relative to the container start, which is the earliest
/// stream — often audio, which can lead the video by a frame or so of priming. Two files whose
/// pictures match frame for frame can therefore present them at different instants, and a sampled
/// VMAF window that pairs frames by timestamp then compares neighbours instead of the same
/// picture. The server builds the measurement with a token where that difference goes, because
/// only the machine holding both files can measure it.</para>
///
/// <para>The same arithmetic as the macOS sidecar's, down to the formatting: the server parses
/// what comes back, and two workers that disagreed about how to write a number would be reporting
/// measurements of different things.</para>
/// </summary>
public static class TimelineLead
{
    public static IReadOnlyList<string> ProbeArguments { get; } =
    [
        "-v", "error",
        "-show_entries", "format=start_time:stream=codec_type,start_time",
        "-of", "json",
    ];

    /// <summary>The video stream's start less the container's, in seconds, or null when either is missing.</summary>
    public static double? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("format", out var format)
                || Seconds(format, "start_time") is not { } container
                || !root.TryGetProperty("streams", out var streams))
            {
                return null;
            }

            foreach (var stream in streams.EnumerateArray())
            {
                if (stream.TryGetProperty("codec_type", out var kind)
                    && kind.ValueKind == JsonValueKind.String
                    && kind.GetString() == "video"
                    && Seconds(stream, "start_time") is { } picture)
                {
                    return picture - container;
                }
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The seconds by which the candidate presents a picture later than the source, formatted the
    /// way the server formats seconds in a filter graph: up to six decimals, no trailing zeros.
    /// </summary>
    public static string Shift(double candidate, double source)
    {
        var difference = candidate - source;
        // In microseconds, which is the unit the filter graph works in: a gap below half a
        // microsecond is no gap at all, and is written as a plain 0 rather than as a long decimal
        // the server would have to parse back.
        var value = Math.Round(difference * 1_000_000) == 0 ? 0 : difference;
        var text = value.ToString("F6", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');
        return text is "-0" or "" ? "0" : text;
    }

    private static double? Seconds(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String when double.TryParse(
                value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }
}
