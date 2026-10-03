using Optimisarr.Core.Verification;
using Optimisarr.Core.Workers;

namespace Optimisarr.Tests;

public sealed class RemoteVerificationEvidenceTests
{
    [Theory]
    [InlineData(null, 400)]
    [InlineData(400, null)]
    [InlineData(0, 400)]
    [InlineData(400, -1)]
    public void Required_decoded_counts_cannot_be_substituted_with_packet_counts(int? source, int? candidate)
    {
        var contract = Contract with { CountVideoFrames = true };
        var evidence = Valid() with { SourceDecodedFrameCount = source, CandidateDecodedFrameCount = candidate,
            SourceVideo = new(true, 0, null, 8, 400), CandidateVideo = new(true, 0, null, 8, 400) };
        Assert.Contains(RemoteVerificationEvidenceValidator.Validate(contract, evidence, Source, Candidate),
            reason => reason.Contains("decoded picture counts"));
    }

    [Fact]
    public void Incomplete_timestamp_evidence_identifies_the_stream_and_missing_presentation_times()
    {
        var reasons = RemoteVerificationEvidenceValidator.ValidateMeasurements(
            Valid() with { SourceVideo = new(true, 0, null, null) }, true);
        Assert.Contains(reasons, reason => reason.Contains("source-video") && reason.Contains("presentation"));
        Assert.DoesNotContain(reasons, reason => reason.Contains("candidate-video"));
    }

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
    [Fact]
    public void Audio_evidence_requires_audio_probes_and_packet_spans_instead_of_picture_measurements()
    {
        const string audio = """{"streams":[{"codec_type":"audio","codec_name":"flac","channels":2,"sample_rate":"48000"}],"format":{"duration":"8"}}""";
        var contract = new RemoteVerificationContract(2, Contract.Id, true);
        var evidence = Valid() with { SourceProbe = audio, CandidateProbe = audio,
            SourceVideo = null, CandidateVideo = null, SourceAudio = new(true, 0, null, 8), CandidateAudio = new(true, 0, null, 8) };
        Assert.Empty(RemoteVerificationEvidenceValidator.Validate(contract, evidence, Source, Candidate));
    }
    [Fact]
    public void Audio_contract_rejects_missing_candidate_packets_video_and_unrequested_contracts()
    {
        const string audio = """{"streams":[{"codec_type":"audio","codec_name":"aac","channels":2,"sample_rate":"48000"}],"format":{"duration":"8"}}""";
        var contract = new RemoteVerificationContract(2, Contract.Id, true);
        var evidence = Valid() with { SourceProbe = audio, CandidateProbe = audio,
            SourceVideo = null, CandidateVideo = null, SourceAudio = new(true, 0, null, 8), CandidateAudio = new(true, 0, null, 8) };
        Assert.Empty(RemoteVerificationEvidenceValidator.Validate(contract, evidence, Source, Candidate));
        foreach (var bad in new[] { evidence with { CandidateAudio = null }, evidence with { CandidateProbe = Probe },
            evidence with { CandidateLoudness = null }, evidence with { CandidateAudio = new(true, 0, null, double.NaN) },
            evidence with { SourceAudio = new(true, 0, null, 0) },
            evidence with { CandidateAudio = new(true, 0, null, -1) },
            evidence with { CandidateProbe = audio.Replace("48000", "0") } })
            Assert.NotEmpty(RemoteVerificationEvidenceValidator.Validate(contract, bad, Source, Candidate));
        Assert.NotEmpty(RemoteVerificationEvidenceValidator.Validate(contract with { Version = 3 }, evidence, Source, Candidate));
        Assert.NotEmpty(RemoteVerificationEvidenceValidator.Validate(contract with { Version = 1 }, evidence, Source, Candidate));
    }

}
