using System.Globalization;
using System.Text.Json;

namespace Optimisarr.Core.Verification;

public sealed record AudioQualityWindow(double StartSeconds, double DurationSeconds);

public static class AudioQualityWindowPlanner
{
    public static IReadOnlyList<AudioQualityWindow> Plan(double durationSeconds)
    {
        if (!double.IsFinite(durationSeconds) || durationSeconds < 1 || durationSeconds > 86400) return [];
        var duration = Math.Floor(durationSeconds * 48000) / 48000;
        if (duration <= 90)
        {
            var count = (int)Math.Ceiling(duration / 30);
            var frames = (long)Math.Floor(duration * 48000);
            var length = frames / count;
            var remainder = frames % count;
            return Enumerable.Range(0, count)
                .Select(i => new AudioQualityWindow((i * length + Math.Min(i, remainder)) / 48000.0,
                    (length + (i < remainder ? 1 : 0)) / 48000.0))
                .ToArray();
        }
        return [new(0, 30), new(Math.Floor((duration - 30) * 24000) / 48000, 30), new(duration - 30, 30)];
    }
}

public sealed record AudioQualityInput(double DurationSeconds, int Channels, int SampleRate, string ChannelLayout)
{
    public static AudioQualityInput? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var streams = doc.RootElement.GetProperty("streams").EnumerateArray().ToArray();
            var audio = streams.Where(s => s.GetProperty("codec_type").GetString() == "audio").ToArray();
            if (audio.Length != 1 || streams.Any(s => s.GetProperty("codec_type").GetString() == "video"
                && (!s.TryGetProperty("disposition", out var d) || !d.TryGetProperty("attached_pic", out var p) || p.GetInt32() != 1))) return null;
            var track = audio[0];
            var channels = track.GetProperty("channels").GetInt32();
            var sampleRate = int.Parse(track.GetProperty("sample_rate").GetString()!, CultureInfo.InvariantCulture);
            var durationText = track.TryGetProperty("duration", out var t) && t.GetString() != "N/A"
                ? t.GetString() : doc.RootElement.GetProperty("format").GetProperty("duration").GetString();
            if (!double.TryParse(durationText, NumberStyles.Float, CultureInfo.InvariantCulture, out var duration)
                || AudioQualityWindowPlanner.Plan(duration).Count == 0 || channels is not (1 or 2) || sampleRate is < 8000 or > 384000) return null;
            var expectedLayout = channels == 1 ? "mono" : "stereo";
            var layout = track.TryGetProperty("channel_layout", out var l) ? l.GetString() : null;
            if (!string.IsNullOrEmpty(layout) && layout != expectedLayout) return null;
            return new(duration, channels, sampleRate, expectedLayout);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or ArgumentNullException)
        { return null; }
    }

    public static string? Incompatibility(AudioQualityInput reference, AudioQualityInput candidate) =>
        reference.Channels != candidate.Channels || reference.ChannelLayout != candidate.ChannelLayout
            ? "The candidate's channels do not match the reference."
            : Math.Abs(reference.DurationSeconds - candidate.DurationSeconds) > 0.1
                ? "The candidate's duration differs by more than the assessment's 100 ms tolerance."
                : null;
}

public static class AudioQualityCommandBuilder
{
    public static IReadOnlyList<string> Decode(string source, string output, AudioQualityWindow window) =>
    [
        "-nostdin", "-hide_banner", "-nostats", "-v", "error", "-xerror", "-n",
        "-ss", window.StartSeconds.ToString("0.########", CultureInfo.InvariantCulture),
        "-i", source, "-map", "0:a:0", "-t", window.DurationSeconds.ToString("0.########", CultureInfo.InvariantCulture),
        "-vn", "-sn", "-dn", "-ar", "48000", "-c:a", "pcm_f32le", "-fs", "11520004", "-f", "f32le", output
    ];
}

public sealed record AudioQualityDistances(int Frames, IReadOnlyList<double> ChannelDistances)
{
    public double WorstChannelDistance => ChannelDistances.Max();
}

public static class AudioQualityResultParser
{
    public const string Revision = "f9e7364df2f6a41f761f513b7ea6be7e2d6f2ce3";
    public const string Preparation = "audio-f32le-48k-native-defaults-v1";

    public static AudioQualityDistances? Parse(string json, int channels, double durationSeconds)
    {
        if (json.Length > 8192 || channels is not (1 or 2) || !double.IsFinite(durationSeconds) || durationSeconds is < 1 or > 30) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() != 1)) return null;
            if (root.GetProperty("schema").GetInt32() != 1 || root.GetProperty("metric").GetString() != "zimtohrli"
                || root.GetProperty("revision").GetString() != Revision || root.GetProperty("sampleRate").GetInt32() != 48000
                || root.GetProperty("channels").GetInt32() != channels || Math.Abs(root.GetProperty("fullScaleSineDb").GetDouble() - 78.3) > 0.0001) return null;
            var frames = root.GetProperty("frames").GetInt32();
            if (Math.Abs(frames - durationSeconds * 48000) > 64) return null;
            var values = root.GetProperty("distances").EnumerateArray().Select(v => v.GetDouble()).ToArray();
            if (values.Length != channels || values.Any(v => !double.IsFinite(v) || v is < 0 or > 1)) return null;
            return new(frames, values);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { return null; }
    }
}
