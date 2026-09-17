using System.Text.Json;
using Optimisarr.Core.Library;
using Optimisarr.Core.Verification;

namespace Optimisarr.Core.Workers;

/// <summary>Lease-specific measurement request. Thresholds remain authoritative on the server.</summary>
public sealed record RemoteVerificationContract(int Version, Guid Id, bool MeasureAudio);

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
    string? Error = null);

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
        if (contract.Version != 1 || contract.Id != evidence.ContractId)
            reasons.Add("Verification evidence belongs to a different or unsupported contract.");
        if (!Matches(sourceSha256, evidence.SourceSha256) || !Matches(candidateSha256, evidence.CandidateSha256))
            reasons.Add("Verification evidence does not match the source and delivered candidate hashes.");
        reasons.AddRange(ValidateMeasurements(evidence, contract.MeasureAudio));
        return reasons;
    }

    public static IReadOnlyList<string> ValidateMeasurements(RemoteVerificationEvidence evidence, bool measureAudio)
    {
        var reasons = new List<string>();
        if (!string.IsNullOrWhiteSpace(evidence.Error)) reasons.Add(evidence.Error);
        if (!ProbeValid(evidence.SourceProbe, out var hasAudio) || !ProbeValid(evidence.CandidateProbe, out _))
            reasons.Add("Both complete video probes are required.");
        if (evidence.Decode is null || evidence.Decode.ErrorCount < 0
            || (evidence.Decode.Healthy && (evidence.Decode.ErrorCount != 0 || evidence.Decode.Error is not null)))
            reasons.Add("A complete decode-health measurement is required.");
        if (!TimestampValid(evidence.SourceVideo) || !TimestampValid(evidence.CandidateVideo))
            reasons.Add("Complete source and candidate video timestamp measurements are required.");
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

    private static bool LoudnessValid(LoudnessResult? value) =>
        value is { Measured: true, Error: null, IntegratedLufs: { } lufs, TruePeakDbtp: { } peak }
        && double.IsFinite(lufs) && double.IsFinite(peak);

    private static bool ProbeValid(string? json, out bool hasAudio)
    {
        hasAudio = false;
        if (string.IsNullOrWhiteSpace(json) || json.Length > 1024 * 1024) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            var probe = MediaProbeService.Parse(json);
            hasAudio = probe.AudioTrackCount > 0;
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
