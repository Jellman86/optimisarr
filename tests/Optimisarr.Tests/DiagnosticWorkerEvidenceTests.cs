using System.Text.Json;
using Optimisarr.Api.Diagnostics;
using Optimisarr.Core.Verification;
using Optimisarr.Core.Workers;
using Optimisarr.Data;

namespace Optimisarr.Tests;

public sealed class DiagnosticWorkerEvidenceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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
