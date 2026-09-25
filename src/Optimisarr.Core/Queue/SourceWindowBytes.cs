using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Optimisarr.Core.Verification;

namespace Optimisarr.Core.Queue;

/// <summary>
/// The bytes one source stream carried inside one sample window, as the container stored them.
/// <see cref="TypeIndex"/> counts streams of the same <see cref="CodecType"/> in file order, which is
/// how FFmpeg's <c>0:a:N</c> and <c>0:s:N</c> specifiers number them.
/// </summary>
public sealed record SampledStreamBytes(string CodecType, int TypeIndex, bool IsPrimaryVideo, long Bytes);

/// <summary>
/// Reads what the source itself spent on the same seconds the adaptive samples encode.
///
/// <para>A sample's size means little on its own: forty seconds of a dark dialogue scene and forty
/// seconds of a snowstorm differ several-fold in any codec. Comparing each sample with the source's
/// own bytes over the same frames cancels that out, which projecting a sample onto the whole
/// file's average bitrate cannot do.</para>
/// </summary>
public static class SourceWindowBytesParser
{
    /// <summary>
    /// Sums packet sizes per stream for packets that start inside <c>[startSeconds, endSeconds)</c>
    /// on the file's own timeline. ffprobe's <c>-read_intervals</c> starts reading at the keyframe
    /// before the window, so earlier packets are present and must be discarded here. Returns null
    /// when the output cannot be read or holds no picture bytes at all, so a forecast abstains
    /// rather than dividing by nothing.
    /// </summary>
    public static IReadOnlyList<SampledStreamBytes>? Parse(string json, double startSeconds, double endSeconds)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array
                || !root.TryGetProperty("packets", out var packets) || packets.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var byIndex = new Dictionary<int, (string Type, int TypeIndex, bool Primary)>();
            var typeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var primaryChosen = false;
            foreach (var stream in streams.EnumerateArray())
            {
                if (!stream.TryGetProperty("index", out var indexElement) || !indexElement.TryGetInt32(out var index))
                {
                    continue;
                }
                var type = stream.TryGetProperty("codec_type", out var typeElement)
                    ? typeElement.GetString() ?? "unknown"
                    : "unknown";
                var typeIndex = typeCounts.GetValueOrDefault(type);
                typeCounts[type] = typeIndex + 1;

                // Cover art is a video stream too, but it is one picture, not the programme. The
                // primary picture is the first moving one, as MediaProbeService chooses it.
                var attachedPicture = stream.TryGetProperty("disposition", out var disposition)
                    && disposition.TryGetProperty("attached_pic", out var attached)
                    && attached.TryGetInt32(out var flag) && flag == 1;
                var primary = !primaryChosen && type == "video" && !attachedPicture;
                primaryChosen |= primary;
                byIndex[index] = (type, typeIndex, primary);
            }

            var totals = new Dictionary<int, long>();
            foreach (var packet in packets.EnumerateArray())
            {
                if (!packet.TryGetProperty("stream_index", out var streamElement)
                    || !streamElement.TryGetInt32(out var streamIndex)
                    || !byIndex.ContainsKey(streamIndex)
                    || (ReadSeconds(packet, "pts_time") ?? ReadSeconds(packet, "dts_time")) is not { } at
                    || at < startSeconds || at >= endSeconds
                    || ReadLong(packet, "size") is not { } size || size <= 0)
                {
                    continue;
                }
                totals[streamIndex] = totals.GetValueOrDefault(streamIndex) + size;
            }

            var result = byIndex
                .Where(entry => totals.ContainsKey(entry.Key))
                .OrderBy(entry => entry.Key)
                .Select(entry => new SampledStreamBytes(
                    entry.Value.Type, entry.Value.TypeIndex, entry.Value.Primary, totals[entry.Key]))
                .ToList();
            return result.Any(stream => stream.IsPrimaryVideo) ? result : null;
        }
    }

    // ffprobe prints numbers as strings in JSON ("pts_time": "12.345000", "size": "4096").
    private static double? ReadSeconds(JsonElement packet, string name) =>
        packet.TryGetProperty(name, out var element)
        && double.TryParse(element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText(),
            NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
        && double.IsFinite(value)
            ? value
            : null;

    private static long? ReadLong(JsonElement packet, string name) =>
        packet.TryGetProperty(name, out var element)
        && long.TryParse(element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText(),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
}

/// <summary>Measures a source's per-stream bytes over the adaptive sample windows.</summary>
public interface ISourceWindowBytesProbe
{
    /// <summary>One entry per window, in order, or null when any window could not be read.</summary>
    Task<IReadOnlyList<IReadOnlyList<SampledStreamBytes>>?> MeasureAsync(
        string path,
        IReadOnlyList<VmafWindow> windows,
        double? containerStartSeconds,
        CancellationToken cancellationToken);
}

/// <summary>
/// Reads packet sizes with ffprobe, one bounded read per window. A metadata-only read of about two
/// minutes of the source, not a decode and not a whole-file scan, so it is cheap next to the sample
/// encodes it sits beside. ffprobe runs with an explicit argument list, never a shell string.
/// </summary>
public sealed class SourceWindowBytesProbe(string? ffprobeCommand = null) : ISourceWindowBytesProbe
{
    private readonly string _ffprobe = string.IsNullOrWhiteSpace(ffprobeCommand) ? "ffprobe" : ffprobeCommand;

    public async Task<IReadOnlyList<IReadOnlyList<SampledStreamBytes>>?> MeasureAsync(
        string path,
        IReadOnlyList<VmafWindow> windows,
        double? containerStartSeconds,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || windows.Count == 0)
        {
            return null;
        }

        var measured = new List<IReadOnlyList<SampledStreamBytes>>(windows.Count);
        foreach (var window in windows)
        {
            if (window.StartSeconds is not { } start || window.DurationSeconds is not { } duration || duration <= 0)
            {
                return null;
            }

            // A sample's `-ss` is relative to the container's start; ffprobe's read interval is not.
            // Transport streams routinely begin seconds, or hours, after zero.
            var from = (containerStartSeconds ?? 0) + start;
            var json = await ReadAsync(path, from, duration, cancellationToken);
            if (json is null || SourceWindowBytesParser.Parse(json, from, from + duration) is not { } streams)
            {
                return null;
            }
            measured.Add(streams);
        }
        return measured;
    }

    /// <summary>
    /// An absolute end, never <c>%+duration</c>: ffprobe counts a relative end from the keyframe it
    /// seeked back to, so a real 720p Blu-ray episode read only to 38.7 of the 40 seconds and
    /// understated the source by about 4%. The margin past the end lets reordered B-frames whose
    /// presentation time is still inside the window arrive; the parser discards the rest.
    /// </summary>
    public static string ReadInterval(double fromSeconds, int durationSeconds) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{fromSeconds:0.###}%{fromSeconds + durationSeconds + ReorderMarginSeconds:0.###}");

    private const double ReorderMarginSeconds = 2;

    private async Task<string?> ReadAsync(string path, double from, int duration, CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = _ffprobe,
            ArgumentList =
            {
                "-v", "error",
                "-read_intervals", ReadInterval(from, duration),
                "-show_entries", "stream=index,codec_type:stream_disposition=attached_pic:packet=stream_index,pts_time,dts_time,size",
                "-of", "json",
                path
            },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }

        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            await stderrTask;
            return process.ExitCode == 0 ? stdout : null;
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception)
            {
                // Best effort; the process is exiting anyway.
            }
            throw;
        }
    }
}
