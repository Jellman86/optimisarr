using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Optimisarr.Core.Library;

namespace Optimisarr.Core.Verification;

/// <summary>Where the primary picture and primary audio end, read from the last part of a file.</summary>
public sealed record SourceTailTimeline(
    double? VideoStartSeconds,
    double? VideoEndSeconds,
    double? AudioStartSeconds,
    double? AudioEndSeconds);

/// <summary>What the pre-encode check concluded about a source's own picture timeline.</summary>
public sealed record SourceTimelineVerdict(bool Short, string? Reason)
{
    public static SourceTimelineVerdict Clear { get; } = new(false, null);
}

/// <summary>
/// Reads the tail of an ffprobe packet listing: the last presentation time of the primary picture
/// and of the primary audio.
///
/// <para>ffprobe seeks a read interval back to the keyframe before it. So a picture stream that
/// stops minutes before the audio still reports its true last packet from a read of only the final
/// seconds: on a real Blu-ray episode, picture ending at 2900.8 s against audio at 3070.2 s, the
/// same as a full scan, in a hundredth of the time.</para>
/// </summary>
public static class SourceTailTimelineParser
{
    public static SourceTailTimeline? Parse(string json)
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

            int? video = null, audio = null;
            double? videoStart = null, audioStart = null;
            foreach (var stream in streams.EnumerateArray())
            {
                if (!stream.TryGetProperty("index", out var indexElement) || !indexElement.TryGetInt32(out var index))
                {
                    continue;
                }
                var type = stream.TryGetProperty("codec_type", out var typeElement) ? typeElement.GetString() : null;
                var attachedPicture = stream.TryGetProperty("disposition", out var disposition)
                    && disposition.TryGetProperty("attached_pic", out var attached)
                    && attached.TryGetInt32(out var flag) && flag == 1;
                // The same streams the verifier measures: the first moving picture, and a:0.
                if (type == "video" && !attachedPicture && video is null)
                {
                    video = index;
                    videoStart = Seconds(stream, "start_time");
                }
                else if (type == "audio" && audio is null)
                {
                    audio = index;
                    audioStart = Seconds(stream, "start_time");
                }
            }

            double? videoEnd = null, audioEnd = null;
            foreach (var packet in packets.EnumerateArray())
            {
                if (!packet.TryGetProperty("stream_index", out var streamElement)
                    || !streamElement.TryGetInt32(out var streamIndex)
                    || Seconds(packet, "pts_time") is not { } pts)
                {
                    continue;
                }
                var end = pts + (Seconds(packet, "duration_time") ?? 0);
                if (streamIndex == video)
                {
                    videoEnd = Math.Max(videoEnd ?? end, end);
                }
                else if (streamIndex == audio)
                {
                    audioEnd = Math.Max(audioEnd ?? end, end);
                }
            }

            return new SourceTailTimeline(videoStart, videoEnd, audioStart, audioEnd);
        }
    }

    // ffprobe prints numbers as strings in JSON, and "N/A" where it has none.
    private static double? Seconds(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && double.TryParse(value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText(),
            NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
        && double.IsFinite(seconds)
            ? seconds
            : null;
}

/// <summary>
/// Decides, before any encoding, whether the source's own picture ends so far short of its audio
/// that verification is certain to reject every encode of it.
///
/// <para>The rule is the verifier's own Source video timeline gate — more than a second and more
/// than 2% short — so the two can never disagree about a file. A source is only held on two
/// independent readings: the cheap tail read must look short, and the full packet scans the
/// verifier itself runs must confirm it. A scan that returns only the opening frames while the
/// stream's own duration agrees with the audio is the known unreliable-scan pattern; it is left to
/// verification, which has the encoded output as further evidence, and never asserted here.</para>
/// </summary>
public static class SourceTimelineJudge
{
    public static bool LooksShort(SourceTailTimeline tail) =>
        SourceTimelineAssessment.NeedsConfirmation(
            Span(tail.VideoEndSeconds, tail.VideoStartSeconds),
            Span(tail.AudioEndSeconds, tail.AudioStartSeconds));

    public static SourceTimelineVerdict Confirm(
        double? videoLastPresentation,
        double? videoStart,
        double? audioLastPresentation,
        double? audioStart,
        double? videoMetadataSeconds)
    {
        if (Span(videoLastPresentation, videoStart) is not { } video
            || Span(audioLastPresentation, audioStart) is not { } audio
            || !SourceTimelineAssessment.NeedsConfirmation(video, audio)
            || SourceTimelineAssessment.IsIndeterminate(video, audio, outputVideoSeconds: null, videoMetadataSeconds))
        {
            return SourceTimelineVerdict.Clear;
        }

        return new SourceTimelineVerdict(true, string.Format(
            CultureInfo.InvariantCulture,
            "Not encoded. Verification failed before encoding: {0}: the source's picture ends at {1:0.#}s "
            + "while its primary audio runs to {2:0.#}s ({3:0.#}% short). Verification rejects any encode of "
            + "a source like this, so none was started. The original is unchanged; replace the file and retry.",
            VerificationEvaluator.SourceVideoTimelineCheckName,
            video,
            audio,
            (audio - video) / audio * 100));
    }

    private static double? Span(double? end, double? start) =>
        end is { } last && double.IsFinite(last) ? Math.Max(0, last - (start ?? 0)) : null;
}

/// <summary>The pre-encode source timeline check, cached per source file.</summary>
public interface ISourceTimelinePreflight
{
    Task<SourceTimelineVerdict> CheckAsync(string path, CancellationToken cancellationToken);
}

/// <summary>
/// Reads a source's tail with ffprobe and, only when that looks short, confirms with the verifier's
/// full packet scans. Cached by path, size and modification time, so a queued source is read once
/// however often workers check in, and a replaced file is read again. Any failure to read clears
/// the source: this check can only save an encode, never block one on missing evidence.
/// </summary>
public sealed class SourceTimelinePreflight(
    IMediaProbeService probe,
    TimestampIntegrityCheck timestamps,
    string? ffprobeCommand = null) : ISourceTimelinePreflight
{
    private const double TailSeconds = 30;
    private readonly string _ffprobe = string.IsNullOrWhiteSpace(ffprobeCommand) ? "ffprobe" : ffprobeCommand;
    private readonly ConcurrentDictionary<(string Path, long Size, DateTime Modified), SourceTimelineVerdict> _cache = new();

    public async Task<SourceTimelineVerdict> CheckAsync(string path, CancellationToken cancellationToken)
    {
        FileInfo file;
        try
        {
            file = new FileInfo(path);
            if (!file.Exists)
            {
                return SourceTimelineVerdict.Clear;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return SourceTimelineVerdict.Clear;
        }

        var key = (file.FullName, file.Length, file.LastWriteTimeUtc);
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var verdict = await MeasureAsync(path, cancellationToken);
        _cache[key] = verdict;
        return verdict;
    }

    private async Task<SourceTimelineVerdict> MeasureAsync(string path, CancellationToken cancellationToken)
    {
        var source = await probe.ProbeAsync(path, cancellationToken);
        if (!source.Success || source.DurationSeconds is not { } duration || duration <= 0)
        {
            return SourceTimelineVerdict.Clear;
        }

        var from = Math.Max(0, (source.ContainerStartSeconds ?? 0) + duration - TailSeconds);
        if (await ReadTailAsync(path, from, cancellationToken) is not { } tail
            || !SourceTimelineJudge.LooksShort(tail))
        {
            return SourceTimelineVerdict.Clear;
        }

        var video = await timestamps.CheckAsync(path, cancellationToken);
        var audio = await timestamps.CheckPrimaryAudioAsync(path, cancellationToken);
        return video.Measured && audio.Measured
            ? SourceTimelineJudge.Confirm(
                video.LastPresentationSeconds,
                source.VideoStartSeconds,
                audio.LastPresentationSeconds,
                source.AudioStartSeconds,
                source.VideoDurationSeconds)
            : SourceTimelineVerdict.Clear;
    }

    private async Task<SourceTailTimeline?> ReadTailAsync(string path, double from, CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = _ffprobe,
            ArgumentList =
            {
                "-v", "error",
                "-read_intervals", $"{from.ToString("0.###", CultureInfo.InvariantCulture)}%",
                "-show_entries",
                "stream=index,codec_type,start_time:stream_disposition=attached_pic:packet=stream_index,pts_time,duration_time",
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

        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            await stderr;
            return process.ExitCode == 0 ? SourceTailTimelineParser.Parse(stdout) : null;
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
