using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Optimisarr.Core.Verification;
using Optimisarr.Core.Workers;
using Optimisarr.Data;

namespace Optimisarr.Api.Diagnostics;

internal sealed record DiagnosticTimestampSummary(
    bool Measured, int? NonMonotonicCount, double? LastPresentationSeconds, int? PacketCount);

internal sealed record DiagnosticDecodeSummary(bool Healthy, int? ErrorCount, bool ErrorPresent);

internal sealed record DiagnosticWorkerVerificationSummary(
    string State,
    Guid? ContractId = null,
    string? SourceSha256 = null,
    string? CandidateSha256 = null,
    bool? MatchesContract = null,
    bool? MatchesReportedQualitySource = null,
    bool? MatchesDeliveredCandidate = null,
    DiagnosticTimestampSummary? SourceVideo = null,
    DiagnosticTimestampSummary? CandidateVideo = null,
    DiagnosticTimestampSummary? SourceAudio = null,
    DiagnosticDecodeSummary? Decode = null,
    bool? ErrorPresent = null,
    string TimelineMethod = "NotRecorded");

/// <summary>Exports retained lease measurements without probe payloads, paths or process text.</summary>
internal static class DiagnosticWorkerEvidence
{
    private const int MaximumStoredCharacters = 1_000_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    public static DiagnosticWorkerVerificationSummary Summarise(JobLease lease)
    {
        if (string.IsNullOrWhiteSpace(lease.VerificationEvidenceJson)) return new("Missing");
        if (lease.VerificationEvidenceJson.Length > MaximumStoredCharacters) return new("Oversized");
        var evidence = Read<RemoteVerificationEvidence>(lease.VerificationEvidenceJson);
        if (evidence is null) return new("Malformed");

        var contract = Read<RemoteVerificationContract>(lease.VerificationContractJson);
        var sourceHash = DiagnosticSafeFields.Sha256(evidence.SourceSha256);
        var candidateHash = DiagnosticSafeFields.Sha256(evidence.CandidateSha256);
        return new(
            "Available",
            evidence.ContractId == Guid.Empty ? null : evidence.ContractId,
            sourceHash,
            candidateHash,
            contract is { Id: var id, Version: 1 } && id != Guid.Empty
                ? id == evidence.ContractId : null,
            HashesMatch(lease.QualitySourceSha256, sourceHash),
            HashesMatch(lease.DeliveredSha256, candidateHash),
            Timestamp(evidence.SourceVideo),
            Timestamp(evidence.CandidateVideo),
            Timestamp(evidence.SourceAudio),
            evidence.Decode is { } decode
                ? new(decode.Healthy, NonNegative(decode.ErrorCount), decode.Error is not null) : null,
            !string.IsNullOrWhiteSpace(evidence.Error));
    }

    // This is the stored JSON's identity, not a semantic hash of an executable command.
    public static string? StoredRecordSha256(string? json) =>
        string.IsNullOrWhiteSpace(json) || json.Length > MaximumStoredCharacters ? null
            : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));

    private static T? Read<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaximumStoredCharacters) return null;
        try { return JsonSerializer.Deserialize<T>(json, JsonOptions); }
        catch (JsonException) { return null; }
    }

    private static bool? HashesMatch(string? expected, string? actual) =>
        DiagnosticSafeFields.Sha256(expected) is not { } hash || actual is null ? null
            : string.Equals(hash, actual, StringComparison.OrdinalIgnoreCase);

    private static DiagnosticTimestampSummary? Timestamp(TimestampCheckResult? scan) => scan is null ? null
        : new(scan.Measured, NonNegative(scan.NonMonotonicCount),
            scan.LastPresentationSeconds is { } last && double.IsFinite(last) ? last : null,
            NonNegative(scan.PacketCount));

    private static int? NonNegative(int? value) => value is >= 0 ? value : null;
}
