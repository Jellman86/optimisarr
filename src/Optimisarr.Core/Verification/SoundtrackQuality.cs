using System.Globalization;
using System.Text.Json;
using Optimisarr.Core.Queue;

namespace Optimisarr.Core.Verification;

public sealed record SoundtrackQualityRequest(IReadOnlyList<int> RemovedSourceAudioIndexes);
public sealed record SoundtrackQualityPair(int SourceAudioIndex, int CandidateAudioIndex, string? Language,
    string? Title, AudioQualityInput Reference, AudioQualityInput Candidate);
public sealed record SoundtrackQualityPlan(IReadOnlyList<SoundtrackQualityPair> Tracks, string? Error);
public sealed record SoundtrackQualityTrack(SoundtrackQualityPair Track, AudioQualityReport Report);
public sealed record SoundtrackQualityReport(IReadOnlyList<SoundtrackQualityTrack> Tracks, string? UnavailableReason,
    bool GateEnabled = false, double? MaximumDistance = null, bool? GatePassed = null);

/// <summary>Output order follows the frozen encode's retained source tracks, never a guessed language match.</summary>
public static class SoundtrackQualityPlanner
{
    public const int MaximumTracks = 8;

    public static SoundtrackQualityPlan Plan(string sourceProbe, string candidateProbe, SoundtrackQualityRequest request)
    {
        SoundtrackQualityPlan Refused(string reason) => new([], reason);
        try
        {
            if (sourceProbe.Length > 1024 * 1024 || candidateProbe.Length > 1024 * 1024)
                return Refused("Soundtrack probes exceed the measurement budget.");
            using var a = JsonDocument.Parse(sourceProbe);
            using var b = JsonDocument.Parse(candidateProbe);
            var source = AudioStreams(a);
            var output = AudioStreams(b);
            var removed = request.RemovedSourceAudioIndexes;
            if (removed is null || removed.Distinct().Count() != removed.Count || removed.Any(index => index < 0 || index >= source.Length))
                return Refused("The retained soundtrack assignment is invalid.");
            var retained = Enumerable.Range(0, source.Length).Where(index => !removed.Contains(index)).ToArray();
            if (retained.Length is < 1 or > MaximumTracks || retained.Length != output.Length)
                return Refused("Assessment requires every retained soundtrack, with at most eight tracks.");
            var pairs = new List<SoundtrackQualityPair>();
            for (var index = 0; index < retained.Length; index++)
            {
                var sourceIndex = retained[index];
                var original = source[sourceIndex];
                var candidate = output[index];
                var reference = AudioQualityInput.ParseTrack(sourceProbe, sourceIndex);
                var distorted = AudioQualityInput.ParseTrack(candidateProbe, index);
                if (reference is null || distorted is null)
                    return Refused($"Soundtrack {index + 1}: assessment supports known-duration mono/stereo tracks. Surround and surround downmix assessment are not available.");
                if (AudioQualityInput.Incompatibility(reference, distorted) is { } mismatch)
                    return Refused($"Soundtrack {index + 1}: {mismatch}");
                if (Language(original) != Language(candidate) || Tag(original, "title") != Tag(candidate, "title")
                    || Comment(original) != Comment(candidate))
                    return Refused($"Soundtrack {index + 1}: language or commentary identity does not match the retained source track.");
                if (Math.Abs((Start(original) - VideoStart(a)) - (Start(candidate) - VideoStart(b))) > 0.05)
                    return Refused($"Soundtrack {index + 1}: start time differs by more than 50 ms. A perceptual score cannot approve a timing shift.");
                var labelLanguage = Tag(original, "language")?.Trim().ToLowerInvariant();
                pairs.Add(new(sourceIndex, index, string.IsNullOrEmpty(labelLanguage) || labelLanguage == "und" ? null : labelLanguage,
                    Tag(original, "title"), reference, distorted));
            }
            return new(pairs, null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or ArgumentNullException)
        { return Refused("Soundtrack probes are incomplete or invalid."); }
    }

    private static JsonElement[] AudioStreams(JsonDocument document) => document.RootElement.GetProperty("streams")
        .EnumerateArray().Where(stream => stream.GetProperty("codec_type").GetString() == "audio").ToArray();
    private static string? Tag(JsonElement stream, string name) => stream.TryGetProperty("tags", out var tags)
        && tags.TryGetProperty(name, out var value) ? value.GetString() : null;
    private static string? Language(JsonElement stream) => Tag(stream, "language") is { } tag && !string.IsNullOrWhiteSpace(tag)
        && !string.Equals(tag, "und", StringComparison.OrdinalIgnoreCase) ? TrackLanguages.Canonicalise(tag) : null;
    private static int Comment(JsonElement stream) => stream.TryGetProperty("disposition", out var disposition)
        && disposition.TryGetProperty("comment", out var value) ? value.GetInt32() : 0;
    private static double VideoStart(JsonDocument probe) => Start(probe.RootElement.GetProperty("streams").EnumerateArray().First(stream =>
        stream.GetProperty("codec_type").GetString() == "video" && (!stream.TryGetProperty("disposition", out var d)
            || !d.TryGetProperty("attached_pic", out var p) || p.GetInt32() != 1)));
    private static double Start(JsonElement stream)
    {
        if (!stream.TryGetProperty("start_time", out var value) || value.GetString() == "N/A") throw new FormatException("Unknown start time.");
        var number = double.Parse(value.GetString()!, CultureInfo.InvariantCulture);
        if (!double.IsFinite(number)) throw new FormatException("Invalid start time.");
        return number;
    }
}
