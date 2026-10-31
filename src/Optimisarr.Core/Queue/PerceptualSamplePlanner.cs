using System.Globalization;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Verification;

namespace Optimisarr.Core.Queue;

public sealed record PerceptualAudioWindow(int StartSeconds, int DurationSeconds);

/// <summary>Portable sample intent shared by local and remote encoding hosts.</summary>
public static class PerceptualSamplePlanner
{
    public static IReadOnlyList<PerceptualAudioWindow> AudioWindows(double duration)
    {
        if (!double.IsFinite(duration) || duration is < 1 or > 86400) return [];
        // Whole seconds match the transcode contract. Any fractional tail is still
        // assessed by final verification; probes are explicitly sampled evidence.
        var seconds = (int)Math.Floor(duration);
        if (seconds <= 90)
        {
            var windows = new List<PerceptualAudioWindow>();
            for (var start = 0; start < seconds; start += 30)
                windows.Add(new(start, Math.Min(30, seconds - start)));
            return windows;
        }
        return [new(0, 30), new((seconds - 30) / 2, 30), new(seconds - 30, 30)];
    }

    public static IReadOnlyList<string> AudioReference(string source, string output, PerceptualAudioWindow window)
    {
        Validate(window);
        var inputSeek = Math.Max(0, window.StartSeconds - 10);
        var outputSeek = window.StartSeconds - inputSeek;
        return ["-nostdin", "-hide_banner", "-nostats", "-v", "error", "-xerror", "-n", "-progress", "pipe:1",
            .. inputSeek == 0 ? Array.Empty<string>() : ["-ss", inputSeek.ToString(CultureInfo.InvariantCulture)],
            "-i", source,
            .. outputSeek == 0 ? Array.Empty<string>() : ["-ss", outputSeek.ToString(CultureInfo.InvariantCulture)],
            "-map", "0:a:0",
            "-t", window.DurationSeconds.ToString(CultureInfo.InvariantCulture), "-vn", "-sn", "-dn", "-map_metadata", "-1",
            "-ar", "48000", "-c:a", "pcm_f32le", "-f", "wav", output];
    }

    /// <summary>One video soundtrack's window, decoded to the same lossless reference as standalone audio.</summary>
    public static IReadOnlyList<string> SoundtrackReference(string source, string output, int sourceAudioIndex, PerceptualAudioWindow window)
    {
        Validate(window);
        if (sourceAudioIndex < 0) throw new ArgumentOutOfRangeException(nameof(sourceAudioIndex));
        var inputSeek = Math.Max(0, window.StartSeconds - 10);
        // A lossy soundtrack decodes in whole packets, and a late first sample shortens a -t cut, so
        // either lands hundreds of samples off the window and the assessment refuses the pair. The
        // filter cuts the window itself, sample-exact, after the decoder context the input seek gives.
        var first = (long)(window.StartSeconds - inputSeek) * 48000;
        var frames = (long)window.DurationSeconds * 48000;
        return ["-nostdin", "-hide_banner", "-nostats", "-v", "error", "-xerror", "-n", "-progress", "pipe:1",
            .. inputSeek == 0 ? Array.Empty<string>() : ["-ss", inputSeek.ToString(CultureInfo.InvariantCulture)],
            "-i", source, "-map", $"0:a:{sourceAudioIndex}", "-vn", "-sn", "-dn", "-map_metadata", "-1",
            "-af", string.Create(CultureInfo.InvariantCulture,
                $"aresample=48000,atrim=start_sample={first},asetpts=PTS-STARTPTS,apad,atrim=end_sample={frames}"),
            "-ar", "48000", "-c:a", "pcm_f32le", "-f", "wav", output];
    }

    /// <summary>
    /// The standalone container each search encoder is measured in. These trim encoder priming the
    /// way the assessment expects; AAC and Opus in Matroska kept it and every sample was refused.
    /// </summary>
    public static string SoundtrackSampleExtension(string encoder) => encoder switch
    {
        "aac" => ".m4a",
        "libopus" => ".opus",
        "libmp3lame" => ".mp3",
        _ => throw new ArgumentException("Unsupported soundtrack search encoder.", nameof(encoder))
    };

    /// <summary>
    /// One soundtrack window encoded as the video job will encode it: same encoder, filter and
    /// channel handling, at the bitrate under test. It encodes the prepared reference rather than
    /// re-reading the source, so both sides hold exactly the same audio before the codec.
    /// </summary>
    public static List<string> SoundtrackCandidate(TranscodeSpec spec, string reference, string output, int bitrate, PerceptualAudioWindow window)
    {
        Validate(window);
        if (spec.Kind != MediaKind.Video || spec.AudioEncoder is null || bitrate <= 0)
            throw new ArgumentException("Soundtrack samples require a video job that re-encodes its audio.");
        List<string> arguments = ["-nostdin", "-hide_banner", "-nostats", "-v", "error", "-xerror", "-n", "-progress", "pipe:1",
            "-i", reference, "-map", "0:a:0", "-vn", "-sn", "-dn", "-map_metadata", "-1", "-c:a", spec.AudioEncoder, "-b:a", $"{bitrate}k"];
        if (spec.DownmixToStereo) arguments.AddRange(["-ac", "2"]);
        if (FfmpegCommandBuilder.ResolveAudioFilter(spec).Chain is { } filter) arguments.AddRange(["-af", filter]);
        arguments.Add(output);
        return arguments;
    }

    public static TranscodeSpec AudioCandidate(TranscodeSpec spec, string output, int bitrate, PerceptualAudioWindow window)
    {
        Validate(window);
        if (spec.Kind != MediaKind.Audio || bitrate <= 0) throw new ArgumentException("Audio samples require a standalone audio encode.");
        return spec with { OutputPath = output, AudioBitrateKbps = bitrate, ClipStartSeconds = window.StartSeconds,
            ClipSeconds = window.DurationSeconds, AudioSampleOnly = true };
    }

    private static void Validate(PerceptualAudioWindow window)
    {
        if (window.StartSeconds < 0 || window.DurationSeconds is < 1 or > 30 || (long)window.StartSeconds + window.DurationSeconds > 86400)
            throw new ArgumentOutOfRangeException(nameof(window));
    }
}

/// <summary>A search is explanatory evidence. It cannot satisfy a final replacement gate.</summary>
public sealed record PerceptualSearchSummary(MediaKind Kind, string Encoder, string SourceSha256,
    double? MinimumImageScore, double? MaximumAudioDistance, IReadOnlyList<PerceptualAudioWindow> AudioWindows,
    PerceptualSearchResult Result, string MeasurementLocation = "Server", int? RetryValue = null,
    bool PreparationBlocked = false, ImagePerceptualReport? SourceImageAssessment = null, int? MinimumValue = null);
