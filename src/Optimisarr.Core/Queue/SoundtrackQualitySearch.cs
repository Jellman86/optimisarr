using System.Text.Json;
using Optimisarr.Core.Verification;

namespace Optimisarr.Core.Queue;

/// <summary>One retained, re-encoded soundtrack, frozen before any bitrate is measured.</summary>
public sealed record SoundtrackSearchTrack(int SourceAudioIndex, int OutputIndex, string? Language, string? Title,
    AudioQualityInput Input, IReadOnlyList<PerceptualAudioWindow> Windows);

public sealed record SoundtrackSearchPlan(string? Encoder, PerceptualQualityPlan? Bitrates,
    IReadOnlyList<SoundtrackSearchTrack> Tracks, string? Unavailable);

public sealed record SoundtrackTrackSearchResult(SoundtrackSearchTrack Track, PerceptualSearchResult Result);

/// <summary>
/// Explanatory evidence for per-soundtrack bitrates. It never authorises replacement: the finished
/// file is still judged track by track by the soundtrack quality gate.
/// </summary>
public sealed record SoundtrackSearchSummary(string? Encoder, int BaselineKbps, double? MaximumDistance, string SourceSha256,
    IReadOnlyList<SoundtrackTrackSearchResult> Tracks, string? Unavailable)
{
    /// <summary>The rate for each output track in order, or null when the library bitrate applies to all of them.</summary>
    public IReadOnlyList<int>? SelectedBitrates() =>
        Unavailable is null && Tracks.Count > 0 ? Tracks.Select(track => track.Result.Decision.SelectedValue).ToArray() : null;
}

/// <summary>
/// Decides which soundtracks of a video re-encode may have their bitrate chosen by measurement. Any
/// retained track that cannot be assessed disables the search for the whole file: the final gate
/// would refuse that file anyway, so a partial search would only spend time.
/// </summary>
public static class SoundtrackSearchPlanner
{
    public static SoundtrackSearchPlan Plan(string sourceProbe, IReadOnlyList<int>? removedSourceAudioIndexes,
        string? encoder, int? baselineKbps, bool downmixToStereo, VerificationPolicy policy)
    {
        SoundtrackSearchPlan Refused(string reason) => new(encoder, null, [], reason);
        if (!policy.SoundtrackQualityGateEnabled || !AudioQualityGate.ValidLimit(policy.MaximumSoundtrackQualityDistance))
            return Refused("Soundtrack quality-first requires the soundtrack quality gate with an explicit limit.");
        if (encoder is not ("aac" or "libopus" or "libmp3lame") || baselineKbps is not { } baseline)
            return Refused("Soundtrack quality-first supports AAC, Opus and MP3 re-encodes.");
        PerceptualQualityPlan bitrates;
        try { bitrates = PerceptualQualityPlan.Audio(encoder, baseline); }
        catch (ArgumentException error) { return Refused(error.Message); }

        int audioCount;
        try
        {
            using var document = JsonDocument.Parse(sourceProbe);
            audioCount = document.RootElement.GetProperty("streams").EnumerateArray()
                .Count(stream => stream.GetProperty("codec_type").GetString() == "audio");
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return Refused("The source probe is incomplete or invalid.");
        }
        var removed = removedSourceAudioIndexes ?? [];
        if (removed.Distinct().Count() != removed.Count || removed.Any(index => index < 0 || index >= audioCount))
            return Refused("The retained soundtrack assignment is invalid.");
        var retained = Enumerable.Range(0, audioCount).Where(index => !removed.Contains(index)).ToArray();
        if (retained.Length is < 1 or > SoundtrackQualityPlanner.MaximumTracks)
            return Refused("Soundtrack quality-first requires between one and eight retained soundtracks.");

        var tracks = new List<SoundtrackSearchTrack>();
        foreach (var (sourceIndex, outputIndex) in retained.Select((index, position) => (index, position)))
        {
            var input = AudioQualityInput.ParseTrack(sourceProbe, sourceIndex);
            var windows = input is null ? [] : PerceptualSamplePlanner.AudioWindows(input.DurationSeconds);
            if (input is null || windows.Count == 0)
                return Refused($"Soundtrack {outputIndex + 1}: quality-first supports known-duration mono/stereo tracks. Surround assessment is not available.");
            // The downmix is one global -ac 2: a no-op only for a track that is already stereo.
            if (downmixToStereo && input.Channels != 2)
                return Refused($"Soundtrack {outputIndex + 1}: with a stereo downmix, quality-first assesses only tracks that are already stereo.");
            var (language, title) = Identity(sourceProbe, sourceIndex);
            tracks.Add(new(sourceIndex, outputIndex, language, title, input, windows));
        }
        return new(encoder, bitrates, tracks, null);
    }

    private static (string? Language, string? Title) Identity(string probe, int audioIndex)
    {
        using var document = JsonDocument.Parse(probe);
        var stream = document.RootElement.GetProperty("streams").EnumerateArray()
            .Where(s => s.GetProperty("codec_type").GetString() == "audio").ElementAt(audioIndex);
        string? Tag(string name) => stream.TryGetProperty("tags", out var tags) && tags.TryGetProperty(name, out var value)
            ? value.GetString() : null;
        var language = Tag("language")?.Trim().ToLowerInvariant();
        return (string.IsNullOrEmpty(language) || language == "und" ? null : language, Tag("title"));
    }
}
