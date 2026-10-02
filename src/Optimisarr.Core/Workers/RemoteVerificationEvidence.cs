using System.Text.Json;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Library;
using Optimisarr.Core.Verification;

namespace Optimisarr.Core.Workers;

/// <summary>Lease-specific measurement request. Thresholds remain authoritative on the server.</summary>
public sealed record RemoteVerificationContract(int Version, Guid Id, bool MeasureAudio, bool MeasureAudioQuality = false,
    SoundtrackQualityRequest? SoundtrackQuality = null)
{
    // Version 1 remains the video contract; version 2 adds standalone audio.
    public MediaKind Kind => Version == 2 ? MediaKind.Audio : MediaKind.Video;
}

/// <summary>Measurements, never a worker-supplied pass verdict. Bound to both transferred files.</summary>
public sealed record RemoteVerificationEvidence(
    Guid ContractId,
    string SourceSha256,
    string CandidateSha256,
    string? SourceProbe = null,
    string? CandidateProbe = null,
    DecodeHealthResult? Decode = null,
    TimestampCheckResult? SourceVideo = null,
    TimestampCheckResult? CandidateVideo = null,
    TimestampCheckResult? SourceAudio = null,
    LoudnessResult? SourceLoudness = null,
    LoudnessResult? CandidateLoudness = null,
    string? Error = null,
    TimestampCheckResult? CandidateAudio = null,
    RemoteAudioQualityEvidence? AudioQuality = null,
    SoundtrackQualityReport? SoundtrackQuality = null);

public static class RemoteVerificationEvidenceValidator
{
    public static IReadOnlyList<string> Validate(
        RemoteVerificationContract contract,
        RemoteVerificationEvidence? evidence,
        string? sourceSha256,
        string? candidateSha256)
    {
        if (evidence is null) return ["The sidecar returned no full verification evidence."];
        var reasons = new List<string>();
        if (contract.Version is not (1 or 2 or 3) || contract.Id != evidence.ContractId
            || (contract.Version == 3 && contract.SoundtrackQuality is null))
            reasons.Add("Verification evidence belongs to a different or unsupported contract.");
        if (!Matches(sourceSha256, evidence.SourceSha256) || !Matches(candidateSha256, evidence.CandidateSha256))
            reasons.Add("Verification evidence does not match the source and delivered candidate hashes.");
        reasons.AddRange(ValidateMeasurements(evidence, contract.MeasureAudio, contract.Kind));
        return reasons;
    }

    public static IReadOnlyList<string> ValidateMeasurements(RemoteVerificationEvidence evidence, bool measureAudio, MediaKind kind = MediaKind.Video)
    {
        var reasons = new List<string>();
        if (!string.IsNullOrWhiteSpace(evidence.Error)) reasons.Add(evidence.Error);
        if (!ProbeValid(evidence.SourceProbe, kind, out var hasAudio) || !ProbeValid(evidence.CandidateProbe, kind, out _))
            reasons.Add($"Both complete {kind.ToString().ToLowerInvariant()} probes are required.");
        if (evidence.Decode is null || evidence.Decode.ErrorCount < 0
            || (evidence.Decode.Healthy && (evidence.Decode.ErrorCount != 0 || evidence.Decode.Error is not null)))
            reasons.Add("A complete decode-health measurement is required.");
        foreach (var (name, scan) in kind == MediaKind.Audio
            ? new[] { ("source-audio", evidence.SourceAudio), ("candidate-audio", evidence.CandidateAudio) }
            : new[] { ("source-video", evidence.SourceVideo), ("candidate-video", evidence.CandidateVideo) })
        {
            var probeJson = name == "source-audio" ? evidence.SourceProbe : evidence.CandidateProbe;
            if (!TimestampValid(scan) || (kind == MediaKind.Audio && !PositiveAudioSpan(scan!, probeJson)))
                reasons.Add(scan is { Measured: true, LastPresentationSeconds: null }
                    ? $"{name}: packet timestamps were read, but no presentation endpoint was available."
                    : $"{name}: a complete, finite timestamp measurement is required.");
        }
        if (evidence.SourceAudio is null || (hasAudio && !TimestampValid(evidence.SourceAudio)))
            reasons.Add("The source audio timestamp check is missing or incomplete.");
        if (measureAudio && (!LoudnessValid(evidence.SourceLoudness) || !LoudnessValid(evidence.CandidateLoudness)))
            reasons.Add("Both requested loudness/true-peak measurements are required.");
        return reasons;
    }

    private static bool Matches(string? expected, string? actual) =>
        expected is { Length: 64 } && actual is { Length: 64 }
        && expected.All(Uri.IsHexDigit) && actual.All(Uri.IsHexDigit)
        && string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);

    private static bool TimestampValid(TimestampCheckResult? value) =>
        value is { Measured: true, NonMonotonicCount: >= 0, LastPresentationSeconds: { } last }
        && double.IsFinite(last);

    private static bool PositiveAudioSpan(TimestampCheckResult scan, string? probeJson)
    {
        if (probeJson is null) return false;
        try
        {
            var start = MediaProbeService.Parse(probeJson).AudioStartSeconds ?? 0;
            var span = scan.LastPresentationSeconds!.Value - start;
            return double.IsFinite(span) && span > 0;
        }
        catch (JsonException) { return false; }
    }

    private static bool LoudnessValid(LoudnessResult? value) =>
        value is { Measured: true, Error: null, IntegratedLufs: { } lufs, TruePeakDbtp: { } peak }
        && double.IsFinite(lufs) && double.IsFinite(peak);

    private static bool ProbeValid(string? json, MediaKind kind, out bool hasAudio)
    {
        hasAudio = false;
        if (string.IsNullOrWhiteSpace(json) || json.Length > 1024 * 1024) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            var probe = MediaProbeService.Parse(json);
            hasAudio = probe.AudioTrackCount > 0;
            if (kind == MediaKind.Audio)
                return probe.Success && hasAudio && probe.MaxAudioChannels is > 0
                    && probe.MaxAudioSampleRate is > 0
                    && probe.AudioTracks.All(track => track.Channels is > 0 && track.SampleRate is > 0)
                    && probe.AudioCodecs.Count == probe.AudioTrackCount && probe.AudioCodecs.All(codec => !string.IsNullOrWhiteSpace(codec))
                    && string.IsNullOrWhiteSpace(probe.VideoCodec)
                    && document.RootElement.TryGetProperty("format", out var audioFormat)
                    && audioFormat.ValueKind == JsonValueKind.Object;
            if (kind != MediaKind.Video) return false;
            if (!probe.Success || probe.Width is not > 0 || probe.Height is not > 0
                || string.IsNullOrWhiteSpace(probe.PixelFormat)) return false;
            return document.RootElement.TryGetProperty("format", out var format)
                && format.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("streams", out var streams)
                && streams.ValueKind == JsonValueKind.Array
                && streams.EnumerateArray().Any(stream =>
                    stream.TryGetProperty("codec_type", out var type) && type.GetString() == "video"
                    && stream.TryGetProperty("codec_name", out var codec) && !string.IsNullOrWhiteSpace(codec.GetString()));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return false; }
    }
}
