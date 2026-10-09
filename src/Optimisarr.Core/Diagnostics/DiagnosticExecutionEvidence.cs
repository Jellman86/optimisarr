using System.Text.Json;
using System.Text.Json.Serialization;
using Optimisarr.Core.Library;
using Optimisarr.Core.Verification;
using Optimisarr.Core.Workers;

namespace Optimisarr.Core.Diagnostics;

public sealed record DiagnosticProbeSnapshot(bool Success, int? Width, int? Height, double? DurationSeconds,
    int? FrameCount, int? AudioTrackCount, int? SubtitleTrackCount, int? MaxAudioChannels);
public sealed record DiagnosticPacketSnapshot(bool Measured, int? NonMonotonicCount, int? PacketCount, double? LastPresentationSeconds);
public sealed record DiagnosticDecodeSnapshot(bool Healthy, int? ErrorCount, bool ErrorPresent);
public sealed record DiagnosticExecutionEvidence(Guid? ContractId, int? ContractVersion, bool? MatchesContract,
    bool? MeasureAudio, bool? MeasureAudioQuality, bool? CountVideoFrames,
    DiagnosticProbeSnapshot? SourceProbe, DiagnosticProbeSnapshot? CandidateProbe,
    DiagnosticPacketSnapshot? SourceVideo, DiagnosticPacketSnapshot? CandidateVideo,
    DiagnosticPacketSnapshot? SourceAudio, DiagnosticPacketSnapshot? CandidateAudio,
    DiagnosticDecodeSnapshot? Decode, int? SourceDecodedFrameCount, int? CandidateDecodedFrameCount, bool ErrorPresent)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
    public static DiagnosticExecutionEvidence? Read(string? contractJson, string? evidenceJson)
    {
        var evidence = ReadJson<RemoteVerificationEvidence>(evidenceJson);
        if (evidence is null) return null;
        var contract = ReadJson<RemoteVerificationContract>(contractJson);
        return new(evidence.ContractId, contract?.Version, contract is null ? null : contract.Id == evidence.ContractId,
            contract?.MeasureAudio, contract?.MeasureAudioQuality, contract?.CountVideoFrames,
            Probe(evidence.SourceProbe), Probe(evidence.CandidateProbe), Packet(evidence.SourceVideo), Packet(evidence.CandidateVideo),
            Packet(evidence.SourceAudio), Packet(evidence.CandidateAudio), evidence.Decode is { } decode
                ? new(decode.Healthy, NonNegative(decode.ErrorCount), decode.Error is not null) : null,
            NonNegative(evidence.SourceDecodedFrameCount), NonNegative(evidence.CandidateDecodedFrameCount), evidence.Error is not null);
    }
    private static T? ReadJson<T>(string? json) where T : class
    {
        if (json is null || json.Length > 1_000_000) return null;
        try { return JsonSerializer.Deserialize<T>(json, Json); }
        catch (JsonException) { return null; }
    }
    private static DiagnosticProbeSnapshot? Probe(string? json)
    {
        if (json is null || json.Length > 1_000_000) return null;
        try
        {
            var p = MediaProbeService.Parse(json);
            return new(p.Success, NonNegative(p.Width), NonNegative(p.Height), Finite(p.DurationSeconds), NonNegative(p.FrameCount),
                NonNegative(p.AudioTrackCount), NonNegative(p.SubtitleTrackCount), NonNegative(p.MaxAudioChannels));
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { return null; }
    }
    private static DiagnosticPacketSnapshot? Packet(TimestampCheckResult? p) => p is null ? null
        : new(p.Measured, NonNegative(p.NonMonotonicCount), NonNegative(p.PacketCount), Finite(p.LastPresentationSeconds));
    private static double? Finite(double? n) => n is { } value && double.IsFinite(value) ? value : null;
    private static int? NonNegative(int? n) => n is >= 0 ? n : null;
}
