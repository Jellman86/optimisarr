using Optimisarr.Api.Queue;
using Optimisarr.Core.Library;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Verification;
using Optimisarr.Core.Workers;

namespace Optimisarr.Tests;

public sealed class SidecarOnlyVerificationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "optimisarr-tests", Guid.NewGuid().ToString("N"));
    private const string Probe = """{"streams":[{"codec_type":"video","codec_name":"hevc","profile":"Main","width":64,"height":64,"pix_fmt":"yuv420p","duration":"8"}],"format":{"duration":"8","format_name":"matroska"}}""";

    private VerificationService Service(VmafShadowService? shadow = null)
    {
        var missing = Path.Combine(_root, "no-media-tool-is-installed");
        return new(new MediaProbeService(missing), new DecodeHealthCheck(missing),
            new TimestampIntegrityCheck(missing), new ReferenceFrameAlignmentProbe(missing),
            new QualityScoreService(missing), new LoudnessService(missing),
            new ImageQualityService(missing), new ImageMetadataService(missing), new TranscodeOptions(missing), shadow);
    }

    private RemoteVerificationEvidence Evidence() => new(Guid.NewGuid(), new('a', 64), new('b', 64),
        Probe, Probe, DecodeHealthResult.Ok, new(true, 0, null, 8), new(true, 0, null, 8), TimestampCheckResult.NotMeasured);

    [Fact]
    public async Task Full_remote_evidence_passes_all_applicable_gates_without_any_installed_media_tools()
    {
        Directory.CreateDirectory(_root);
        var output = Path.Combine(_root, "candidate.mkv");
        await File.WriteAllTextAsync(output, "candidate");
        var original = new OriginalSnapshot(Path.Combine(_root, "unread-source.mkv"), 1000, 8, 0, 0, false, false, ExpectedVideoCodec: "hevc");
        var outcome = await Service().VerifyAsync(original, output, VerificationPolicy.Default,
            CancellationToken.None, remoteEvidence: Evidence());
        Assert.True(outcome.Report.Passed, string.Join("; ", outcome.Report.Checks.Select(c => c.Detail)));
    }

    [Fact]
    public async Task Missing_remote_VMAF_is_a_hard_failure_instead_of_local_measurement()
    {
        var original = new OriginalSnapshot("unread", 1000, 8, 0, 0, false, false, ExpectedVideoCodec: "hevc");
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => Service().VerifyAsync(original,
            "unread", VerificationPolicy.Default with { QualityGateEnabled = true },
            CancellationToken.None, remoteEvidence: Evidence()));
        Assert.Contains("fallback is disabled", exception.Message);
    }

    [Theory]
    [InlineData(95, true)]
    [InlineData(20, false)]
    public async Task Explicit_server_research_failure_preserves_the_workers_authoritative_quality_verdict(double score, bool passes)
    {
        Directory.CreateDirectory(_root);
        var output = Path.Combine(_root, "shadow-candidate.mkv");
        await File.WriteAllTextAsync(output, "candidate");
        var original = new OriginalSnapshot(Path.Combine(_root, "source.mkv"), 1000, 8,
            0, 0, false, false, ExpectedVideoCodec: "hevc");
        var calls = 0;
        using var shadow = new VmafShadowService(true, (_, _, _, _) =>
        {
            calls++;
            return Task.FromResult(QualityResult.Failed("Research binary cannot load the model"));
        });
        var probe = Probe.Replace("\"duration\":\"8\"", "\"avg_frame_rate\":\"24/1\",\"duration\":\"8\"");
        var evidence = Evidence() with { SourceProbe = probe, CandidateProbe = probe };
        var remoteQuality = new RemoteQuality(QualityResult.Ok(new(score, score, score, null, null,
            "vmaf_v0.6.1", "worker", score, 192)), "worker");
        var policy = VerificationPolicy.Default with { QualityGateEnabled = true };
        var expected = await Service().VerifyAsync(original, output, policy, default,
            remoteQuality: remoteQuality, remoteEvidence: evidence);
        var actual = await Service(shadow).VerifyAsync(original, output, policy, default,
            remoteQuality: remoteQuality, remoteEvidence: evidence);

        Assert.Equal(1, calls);
        Assert.Equal(passes, actual.Report.Passed);
        Assert.Equal(expected.Report.Checks, actual.Report.Checks);
        Assert.Equal(expected.Report.Vmaf, actual.Report.Vmaf);
        Assert.Equal("Unavailable", actual.Report.ShadowVmaf!.Status);
        Assert.Equal("Server", actual.Report.ShadowVmaf.MeasurementLocation);
    }

    [Fact]
    public async Task Research_is_never_a_fallback_for_missing_worker_verification()
    {
        using var shadow = new VmafShadowService(true, (_, _, _, _) => throw new Exception("Research must not run"));
        var original = new OriginalSnapshot("unread", 1000, 8, 0, 0, false, false, ExpectedVideoCodec: "hevc");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(shadow).VerifyAsync(original,
            "unread", VerificationPolicy.Default with { QualityGateEnabled = true },
            default, remoteEvidence: Evidence()));
    }

    [Fact]
    public async Task Missing_decode_evidence_never_invokes_a_local_decoder()
    {
        var original = new OriginalSnapshot("unread", 1000, 8, 0, 0, false, false, ExpectedVideoCodec: "hevc");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().VerifyAsync(original,
            "unread", VerificationPolicy.Default, CancellationToken.None,
            remoteEvidence: Evidence() with { Decode = null }));
    }

    [Fact]
    public async Task Implausible_worker_source_packet_span_is_held_as_indeterminate()
    {
        const string probe = """{"streams":[{"codec_type":"video","codec_name":"hevc","width":64,"height":64,"pix_fmt":"yuv420p"},{"codec_type":"audio","codec_name":"aac","channels":2,"sample_rate":"48000"}],"format":{"duration":"1279.24","format_name":"matroska"}}""";
        Directory.CreateDirectory(_root);
        var output = Path.Combine(_root, "candidate.mkv");
        await File.WriteAllTextAsync(output, "candidate");
        var original = new OriginalSnapshot(Path.Combine(_root, "source.mkv"), 1000, 1279.24,
            1, 0, false, false, ExpectedVideoCodec: "hevc");
        var evidence = Evidence() with
        {
            SourceProbe = probe,
            CandidateProbe = probe,
            SourceVideo = new TimestampCheckResult(true, 0, null, 0.08),
            SourceAudio = new TimestampCheckResult(true, 0, null, 1277.27),
            CandidateVideo = new TimestampCheckResult(true, 0, null, 1279.24)
        };

        var outcome = await Service().VerifyAsync(original, output, VerificationPolicy.Default,
            CancellationToken.None, remoteEvidence: evidence);

        Assert.False(outcome.Report.Passed);
        var timeline = outcome.Report.Checks.Single(check => check.Name == "Source video timeline");
        Assert.Equal(CheckOutcome.Failed, timeline.Outcome);
        Assert.Contains("indeterminate", timeline.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(outcome.Report.Checks, check => check.Name == "Tail integrity");
    }

    [Fact]
    public async Task Corrupt_candidate_skips_quality_work_even_when_a_score_was_supplied()
    {
        Directory.CreateDirectory(_root);
        var output = Path.Combine(_root, "corrupt-candidate.mkv");
        await File.WriteAllTextAsync(output, "candidate");
        var original = new OriginalSnapshot(Path.Combine(_root, "source.mkv"), 1000, 8,
            0, 0, false, false, ExpectedVideoCodec: "hevc");
        var evidence = Evidence() with
        {
            Decode = DecodeHealthResult.Unhealthy("AV1 parser error", 100)
        };
        var scores = new QualityScores(95, 95, 95, 45, 0.99);
        var remoteQuality = new RemoteQuality(QualityResult.Ok(scores), "worker");

        var outcome = await Service().VerifyAsync(original, output,
            VerificationPolicy.Default with { QualityGateEnabled = true },
            CancellationToken.None, remoteQuality: remoteQuality, remoteEvidence: evidence);

        Assert.False(outcome.Report.Passed);
        Assert.Contains(outcome.Report.Checks,
            check => check.Name == "Decode health" && check.Outcome == CheckOutcome.Failed);
        Assert.Contains(outcome.Report.Checks,
            check => check.Name == "Perceptual quality (VMAF)"
                && check.Outcome == CheckOutcome.Failed
                && check.Detail.Contains("decode", StringComparison.OrdinalIgnoreCase));
    }


    [Fact]
    public async Task Opt_in_audio_reporting_never_reads_remote_media_when_the_worker_omits_observations()
    {
        const string audio = """{"streams":[{"codec_type":"audio","codec_name":"aac","channels":2,"sample_rate":"48000","duration":"8"}],"format":{"duration":"8","format_name":"mov,mp4,m4a,3gp,3g2,mj2"}}""";
        Directory.CreateDirectory(_root);
        var output = Path.Combine(_root, "candidate.m4a");
        await File.WriteAllTextAsync(output, "candidate");
        var original = new OriginalSnapshot("unread-source.flac", 1000, 8, 1, 0, false, false,
            Kind: MediaKind.Audio, VideoReencoded: false);
        var evidence = Evidence() with { SourceProbe = audio, CandidateProbe = audio,
            SourceVideo = null, CandidateVideo = null, SourceAudio = new(true, 0, null, 8), CandidateAudio = new(true, 0, null, 8) };
        var baseline = await Service().VerifyAsync(original, output, VerificationPolicy.Default, default, remoteEvidence: evidence);
        var actual = await Service().VerifyAsync(original, output,
            VerificationPolicy.Default with { AudioQualityReportingEnabled = true }, default, remoteEvidence: evidence);
        Assert.Equal(baseline.Report.Checks, actual.Report.Checks);
        Assert.True(actual.Report.Passed);
        Assert.Equal("Worker", actual.Report.AudioQuality!.MeasurementLocation);
        Assert.Contains("no audio quality report", actual.Report.AudioQuality.UnavailableReason);
    }

    [Fact]
    public async Task Audio_worker_evidence_is_evaluated_without_server_media_tools_or_VMAF()
    {
        const string audio = """{"streams":[{"codec_type":"audio","codec_name":"aac","channels":2,"sample_rate":"48000","duration":"8"}],"format":{"duration":"8","format_name":"mov,mp4,m4a,3gp,3g2,mj2"}}""";
        Directory.CreateDirectory(_root);
        var output = Path.Combine(_root, "candidate.m4a");
        await File.WriteAllTextAsync(output, "candidate");
        var original = new OriginalSnapshot("unread-source.flac", 1000, 8, 1, 0, false, false,
            Kind: MediaKind.Audio, VideoReencoded: false);
        var evidence = Evidence() with { SourceProbe = audio, CandidateProbe = audio,
            SourceVideo = null, CandidateVideo = null, SourceAudio = new(true, 0, null, 8), CandidateAudio = new(true, 0, null, 8) };
        var outcome = await Service().VerifyAsync(original, output,
            VerificationPolicy.Default with { QualityGateEnabled = true }, default, remoteEvidence: evidence);
        Assert.True(outcome.Report.Passed, string.Join("; ", outcome.Report.Checks.Select(c => c.Detail)));
    }

    [Fact]
    public async Task Audio_worker_cannot_deliver_a_different_codec_than_the_frozen_encode_plan()
    {
        const string audio = """{"streams":[{"codec_type":"audio","codec_name":"aac","channels":2,"sample_rate":"48000"}],"format":{"duration":"8"}}""";
        Directory.CreateDirectory(_root);
        var output = Path.Combine(_root, "wrong.opus");
        await File.WriteAllTextAsync(output, "candidate");
        var original = new OriginalSnapshot("unread-source.flac", 1000, 8, 1, 0, false, false,
            Kind: MediaKind.Audio, VideoReencoded: false, ExpectedAudioCodec: "opus");
        var evidence = Evidence() with { SourceProbe = audio, CandidateProbe = audio,
            SourceVideo = null, CandidateVideo = null, SourceAudio = new(true, 0, null, 8), CandidateAudio = new(true, 0, null, 8) };
        var outcome = await Service().VerifyAsync(original, output, VerificationPolicy.Default, default, remoteEvidence: evidence);
        Assert.False(outcome.Report.Passed);
        Assert.Contains(outcome.Report.Checks, check => check.Name == "Audio codec" && check.Outcome == CheckOutcome.Failed);
    }

    [Theory]
    [InlineData("decode")]
    [InlineData("timestamps")]
    [InlineData("duration")]
    [InlineData("channels")]
    [InlineData("loudness")]
    [InlineData("clipping")]
    public async Task Failed_audio_worker_measurements_block_acceptance_without_local_fallback(string failure)
    {
        const string audio = """{"streams":[{"codec_type":"audio","codec_name":"aac","channels":2,"sample_rate":"48000"}],"format":{"duration":"8"}}""";
        Directory.CreateDirectory(_root);
        var output = Path.Combine(_root, "candidate.m4a");
        await File.WriteAllTextAsync(output, "candidate");
        var original = new OriginalSnapshot("unread-source.flac", 1000, 8, 1, 0, false, false,
            Kind: MediaKind.Audio, VideoReencoded: false, ExpectedAudioCodec: "aac");
        var evidence = Evidence() with { SourceProbe = audio, CandidateProbe = audio,
            SourceVideo = null, CandidateVideo = null, SourceAudio = new(true, 0, null, 8), CandidateAudio = new(true, 0, null, 8),
            SourceLoudness = LoudnessResult.Ok(-20, -5), CandidateLoudness = LoudnessResult.Ok(-20, -5) };
        evidence = failure switch
        {
            "decode" => evidence with { Decode = DecodeHealthResult.Unhealthy("corrupt audio", 1) },
            "timestamps" => evidence with { CandidateAudio = new(true, 1, "regressed packet", 8) },
            "duration" => evidence with { CandidateAudio = new(true, 0, null, 4) },
            "channels" => evidence with { CandidateProbe = audio.Replace("\"channels\":2", "\"channels\":1") },
            "loudness" => evidence with { CandidateLoudness = LoudnessResult.Ok(-26, -5) },
            "clipping" => evidence with { CandidateLoudness = LoudnessResult.Ok(-20, 1) },
            _ => throw new ArgumentException(failure)
        };
        var policy = VerificationPolicy.Default with { AudioLoudnessGateEnabled = true, AudioClippingGateEnabled = true };
        var outcome = await Service().VerifyAsync(original, output, policy, default, remoteEvidence: evidence);
        Assert.False(outcome.Report.Passed);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
