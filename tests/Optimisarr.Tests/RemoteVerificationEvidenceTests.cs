using Optimisarr.Core.Verification;
using Optimisarr.Core.Workers;

namespace Optimisarr.Tests;

public sealed class RemoteVerificationEvidenceTests
{
    private static readonly RemoteVerificationContract Contract = new(1, Guid.NewGuid(), true);
    private static readonly string Source = new('a', 64);
    private static readonly string Candidate = new('b', 64);
    private const string Probe = """{"streams":[{"codec_type":"video","codec_name":"hevc","width":64,"height":64,"pix_fmt":"yuv420p"}],"format":{"duration":"8"}}""";
    private static RemoteVerificationEvidence Valid() => new(
        Contract.Id, Source, Candidate, Probe, Probe, DecodeHealthResult.Ok,
        new(true, 0, null, 8), new(true, 0, null, 8), TimestampCheckResult.NotMeasured,
        LoudnessResult.Ok(-20, -1), LoudnessResult.Ok(-20, -1));

    [Fact]
    public void Complete_evidence_is_bound_to_the_issued_contract_and_both_files()
    {
        Assert.Empty(RemoteVerificationEvidenceValidator.Validate(Contract, Valid(), Source, Candidate));
        Assert.NotEmpty(RemoteVerificationEvidenceValidator.Validate(Contract, Valid() with { ContractId = Guid.NewGuid() }, Source, Candidate));
        Assert.NotEmpty(RemoteVerificationEvidenceValidator.Validate(Contract, Valid() with { SourceSha256 = Candidate }, Source, Candidate));
        Assert.NotEmpty(RemoteVerificationEvidenceValidator.Validate(Contract, Valid() with { CandidateSha256 = Source }, Source, Candidate));
    }

    [Fact]
    public void Missing_measurements_and_tool_failures_cannot_turn_into_local_fallback()
    {
        foreach (var evidence in new RemoteVerificationEvidence?[] {
            null, Valid() with { Decode = null }, Valid() with { SourceVideo = null },
            Valid() with { CandidateVideo = TimestampCheckResult.NotMeasured },
            Valid() with { SourceProbe = "{}" }, Valid() with { SourceLoudness = null },
            Valid() with { Error = "ffprobe unavailable" } })
            Assert.NotEmpty(RemoteVerificationEvidenceValidator.Validate(Contract, evidence, Source, Candidate));
    }

    [Fact]
    public void A_completed_negative_measurement_is_evidence_not_a_worker_claim_that_it_passed()
    {
        var evidence = Valid() with { Decode = DecodeHealthResult.Unhealthy("Damaged frame", 2) };
        Assert.Empty(RemoteVerificationEvidenceValidator.Validate(Contract, evidence, Source, Candidate));
        Assert.False(evidence.Decode.Healthy);
    }

    [Fact]
    public void Present_but_incomplete_measurements_are_rejected()
    {
        foreach (var evidence in new[] {
            Valid() with { SourceLoudness = LoudnessResult.Failed("No filter") },
            Valid() with { CandidateLoudness = LoudnessResult.Ok(double.NaN, -1) },
            Valid() with { CandidateLoudness = LoudnessResult.Ok(-20, null) },
            Valid() with { Decode = new(true, "decoder unavailable", 0) },
            Valid() with { SourceProbe = Probe.Replace("\"width\":64,", "") },
            Valid() with { SourceProbe = Probe.Replace("}],", "},{\"codec_type\":\"audio\",\"codec_name\":\"aac\"}],") }
        })
            Assert.NotEmpty(RemoteVerificationEvidenceValidator.Validate(Contract, evidence, Source, Candidate));
    }

    [Fact]
    public void Nonfinite_or_negative_timestamp_evidence_is_rejected()
    {
        Assert.NotEmpty(RemoteVerificationEvidenceValidator.Validate(Contract,
            Valid() with { CandidateVideo = new(true, -1, null, 8) }, Source, Candidate));
        Assert.NotEmpty(RemoteVerificationEvidenceValidator.Validate(Contract,
            Valid() with { CandidateVideo = new(true, 0, null, double.NaN) }, Source, Candidate));
    }
}
