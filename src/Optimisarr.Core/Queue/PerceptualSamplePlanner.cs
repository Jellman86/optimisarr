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
