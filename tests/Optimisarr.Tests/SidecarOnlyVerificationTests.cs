using Optimisarr.Api.Queue;
using Optimisarr.Core.Library;
using Optimisarr.Core.Verification;
using Optimisarr.Core.Workers;

namespace Optimisarr.Tests;

public sealed class SidecarOnlyVerificationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "optimisarr-tests", Guid.NewGuid().ToString("N"));
    private const string Probe = """{"streams":[{"codec_type":"video","codec_name":"hevc","profile":"Main","width":64,"height":64,"pix_fmt":"yuv420p","duration":"8"}],"format":{"duration":"8","format_name":"matroska"}}""";

    private VerificationService Service()
    {
        var missing = Path.Combine(_root, "no-media-tool-is-installed");
        return new(new MediaProbeService(missing), new DecodeHealthCheck(missing),
            new TimestampIntegrityCheck(missing), new ReferenceFrameAlignmentProbe(missing),
            new QualityScoreService(missing), new LoudnessService(missing),
            new ImageQualityService(missing), new ImageMetadataService(missing), new TranscodeOptions(missing));
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

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
