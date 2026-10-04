using System.Text.Json;
using Optimisarr.Api.Diagnostics;
using Optimisarr.Core.Verification;
using Optimisarr.Core.Workers;
using Optimisarr.Data;

namespace Optimisarr.Tests;

public sealed class DiagnosticWorkerEvidenceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    public void Every_supported_contract_reports_the_record_identity_comparison(int version, bool sameId)
    {
        var contract = new RemoteVerificationContract(version, Guid.NewGuid(), false,
            SoundtrackQuality: version == 3 ? new SoundtrackQualityRequest([]) : null);
        var evidence = new RemoteVerificationEvidence(sameId ? contract.Id : Guid.NewGuid(), new string('a', 64), new string('b', 64));
        var summary = DiagnosticWorkerEvidence.Summarise(Lease(contract, evidence));
        Assert.Equal(sameId, summary.MatchesContract);
        Assert.Equal(version, summary.ContractVersion);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(100)]
    public void Unsupported_contract_versions_are_disclosed_without_an_identity_match(int version)
    {
        var contract = new RemoteVerificationContract(version, Guid.NewGuid(), false, CountVideoFrames: true);
        var summary = DiagnosticWorkerEvidence.Summarise(Lease(contract,
            new RemoteVerificationEvidence(contract.Id, new string('a', 64), new string('b', 64))));
        Assert.Equal(version > 0 ? (int?)version : null, summary.ContractVersion);
        Assert.Null(summary.MatchesContract);
        Assert.Null(summary.CountVideoFramesRequested);
    }

    [Fact]
    public void A_soundtrack_contract_without_its_request_does_not_claim_a_match()
    {
        var contract = new RemoteVerificationContract(3, Guid.NewGuid(), false);
        var summary = DiagnosticWorkerEvidence.Summarise(Lease(contract,
            new RemoteVerificationEvidence(contract.Id, new string('a', 64), new string('b', 64))));
        Assert.Equal(3, summary.ContractVersion);
        Assert.Null(summary.MatchesContract);
        Assert.Null(summary.CountVideoFramesRequested);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Missing_contracts_and_empty_contract_ids_do_not_infer_request_flags(bool emptyId)
    {
        var lease = Lease(new RemoteVerificationContract(1, Guid.Empty, false, CountVideoFrames: true),
            new RemoteVerificationEvidence(Guid.NewGuid(), new string('a', 64), new string('b', 64)));
        if (!emptyId) lease.VerificationContractJson = null;
        var summary = DiagnosticWorkerEvidence.Summarise(lease);
        Assert.Equal(emptyId ? (int?)1 : null, summary.ContractVersion);
        Assert.Null(summary.MatchesContract);
        Assert.Null(summary.CountVideoFramesRequested);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(0, 0)]
    [InlineData(400, 398)]
    [InlineData(-1, -2)]
    public void Decoded_picture_counts_are_distinct_from_packets_and_preserve_missing_or_zero(int? source, int? candidate)
    {
        var contract = new RemoteVerificationContract(1, Guid.NewGuid(), false, CountVideoFrames: true);
        var evidence = new RemoteVerificationEvidence(contract.Id, new string('a', 64), new string('b', 64),
            SourceVideo: new TimestampCheckResult(true, 0, "secret", 16, 390),
            CandidateVideo: new TimestampCheckResult(true, 0, "secret", 16, 390),
            SourceDecodedFrameCount: source, CandidateDecodedFrameCount: candidate);
        var summary = DiagnosticWorkerEvidence.Summarise(Lease(contract, evidence));
        Assert.True(summary.CountVideoFramesRequested);
        Assert.Equal(source is >= 0 ? source : null, summary.SourceDecodedFrameCount);
        Assert.Equal(candidate is >= 0 ? candidate : null, summary.CandidateDecodedFrameCount);
        Assert.Equal(390, summary.SourceVideo!.PacketCount);
        Assert.Equal(390, summary.CandidateVideo!.PacketCount);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(summary));
    }

    [Fact]
    public void Candidate_audio_numeric_measurements_are_kept_without_process_text()
    {
        var contract = new RemoteVerificationContract(2, Guid.NewGuid(), false);
        var evidence = new RemoteVerificationEvidence(contract.Id, new string('a', 64), new string('b', 64),
            CandidateAudio: new TimestampCheckResult(true, 2, "Bearer secret-token /private/media", 80.24, 2000));
        var summary = DiagnosticWorkerEvidence.Summarise(Lease(contract, evidence));
        Assert.Equal(new DiagnosticTimestampSummary(true, 2, 80.24, 2000), summary.CandidateAudio);
        Assert.Null(summary.SourceDecodedFrameCount);
        Assert.Null(summary.CandidateDecodedFrameCount);
        Assert.Equal("NotRecorded", summary.TimelineMethod);
        Assert.DoesNotContain("secret-token", JsonSerializer.Serialize(summary));
        Assert.DoesNotContain("/private", JsonSerializer.Serialize(summary));
    }

    [Fact]
    public void Export_keeps_numeric_packet_evidence_and_hash_binding_without_free_text()
    {
        var contract = new RemoteVerificationContract(1, Guid.NewGuid(), false);
        var evidence = new RemoteVerificationEvidence(contract.Id, new string('a', 64), new string('b', 64),
            SourceProbe: "Bearer private-token", CandidateProbe: "/private/movie.mkv",
            Decode: new DecodeHealthResult(false, "Bearer private-token", 4),
            SourceVideo: new TimestampCheckResult(true, 0, "Bearer private-token", 1290.04, 32250),
            CandidateVideo: new TimestampCheckResult(true, 2, "/private/movie.mkv", 1289.84, 32248),
            SourceAudio: TimestampCheckResult.NotMeasured,
            Error: "Bearer private-token");
        var result = DiagnosticWorkerEvidence.Summarise(Lease(contract, evidence));

        Assert.Equal("Available", result.State);
        Assert.True(result.MatchesContract);
        Assert.True(result.MatchesReportedQualitySource);
        Assert.True(result.MatchesDeliveredCandidate);
        Assert.Equal(1290.04, result.SourceVideo!.LastPresentationSeconds);
        Assert.Equal(32250, result.SourceVideo.PacketCount);
        Assert.Equal(2, result.CandidateVideo!.NonMonotonicCount);
        Assert.True(result.ErrorPresent);
        Assert.Equal("NotRecorded", result.TimelineMethod);
        Assert.Equal(4, result.Decode!.ErrorCount);
        var json = JsonSerializer.Serialize(result, JsonOptions);
        Assert.DoesNotContain("private-token", json);
        Assert.DoesNotContain("/private", json);
    }

    [Fact]
    public void Hash_or_contract_mismatch_is_visible_without_authorising_a_candidate()
    {
        var contract = new RemoteVerificationContract(1, Guid.NewGuid(), false);
        var evidence = new RemoteVerificationEvidence(Guid.NewGuid(), new string('c', 64), new string('d', 64));
        var result = DiagnosticWorkerEvidence.Summarise(Lease(contract, evidence));
        Assert.False(result.MatchesContract);
        Assert.False(result.MatchesReportedQualitySource);
        Assert.False(result.MatchesDeliveredCandidate);
    }

    [Theory]
    [InlineData(null, "Missing")]
    [InlineData("", "Missing")]
    [InlineData("null", "Malformed")]
    [InlineData("[]", "Malformed")]
    [InlineData("{", "Malformed")]
    public void Missing_and_unreadable_records_are_explicit(string? json, string state)
    {
        var result = DiagnosticWorkerEvidence.Summarise(new JobLease { VerificationEvidenceJson = json });
        Assert.Equal(state, result.State);
        Assert.Null(result.SourceVideo);
        Assert.Null(result.MatchesContract);
    }

    [Fact]
    public void Oversized_records_are_not_parsed_or_copied()
    {
        var result = DiagnosticWorkerEvidence.Summarise(new JobLease
        {
            VerificationEvidenceJson = new string('x', 1_000_001)
        });
        Assert.Equal("Oversized", result.State);
        Assert.True(JsonSerializer.Serialize(result).Length < 1500);
    }

    [Fact]
    public void Invalid_numeric_fields_and_hashes_do_not_enter_the_export()
    {
        var evidence = new RemoteVerificationEvidence(Guid.Empty, "Bearer private-token", "/private/movie.mkv",
            SourceVideo: new TimestampCheckResult(true, -1, null, double.PositiveInfinity, -1));
        var options = new JsonSerializerOptions(JsonOptions)
        {
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
        };
        var result = DiagnosticWorkerEvidence.Summarise(new JobLease
        {
            VerificationEvidenceJson = JsonSerializer.Serialize(evidence, options)
        });
        Assert.Null(result.SourceSha256);
        Assert.Null(result.CandidateSha256);
        Assert.Null(result.SourceVideo!.LastPresentationSeconds);
        Assert.Null(result.SourceVideo.PacketCount);
        Assert.Null(result.SourceVideo.NonMonotonicCount);
        Assert.Null(result.ContractId);
    }

    [Fact]
    public void Unavailable_expected_hashes_and_malformed_contract_are_unknown_not_a_match()
    {
        var evidence = new RemoteVerificationEvidence(Guid.NewGuid(), new string('a', 64), new string('b', 64));
        var result = DiagnosticWorkerEvidence.Summarise(new JobLease
        {
            VerificationEvidenceJson = JsonSerializer.Serialize(evidence, JsonOptions),
            VerificationContractJson = "secret-token"
        });
        Assert.Null(result.MatchesContract);
        Assert.Null(result.MatchesReportedQualitySource);
        Assert.Null(result.MatchesDeliveredCandidate);
    }

    [Fact]
    public void Stored_record_fingerprint_is_stable_and_never_exports_its_payload()
    {
        var hash = DiagnosticWorkerEvidence.StoredRecordSha256("Bearer private-token");
        Assert.Equal(64, hash!.Length);
        Assert.Equal(hash, DiagnosticWorkerEvidence.StoredRecordSha256("Bearer private-token"));
        Assert.NotEqual(hash, DiagnosticWorkerEvidence.StoredRecordSha256("Different payload"));
        Assert.Null(DiagnosticWorkerEvidence.StoredRecordSha256(null));
        Assert.Null(DiagnosticWorkerEvidence.StoredRecordSha256(new string('x', 1_000_001)));
    }

    private static JobLease Lease(RemoteVerificationContract contract, RemoteVerificationEvidence evidence) => new()
    {
        VerificationContractJson = JsonSerializer.Serialize(contract, JsonOptions),
        VerificationEvidenceJson = JsonSerializer.Serialize(evidence, JsonOptions),
        QualitySourceSha256 = new string('a', 64),
        DeliveredSha256 = new string('b', 64)
    };
}
