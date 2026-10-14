using Optimisarr.Core.Domain;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Verification;
using Optimisarr.Core.Workers;

namespace Optimisarr.Tests;

public sealed class RemotePerceptualProbeTests
{
    private static readonly string Source = new('a', 64), Candidate = new('b', 64), Reference = new('c', 64), Tool = new('d', 64);
    private static RemotePerceptualSearchContract ImageContract() => new(Guid.NewGuid(), MediaKind.Image, "libwebp",
        PerceptualQualityPlan.Image("libwebp", 80), VerificationPolicy.Default with
        { ImagePerceptualGateEnabled = true, MinimumImagePerceptualScore = 85, ImageQualityGateEnabled = true, MinimumImageSsim = 0.9 },
        [], SourceImage: new(32, 32, false));
    private static RemoteImagePerceptualEvidence Image(double score, string candidate) => RemoteImagePerceptualEvidence.From(
        new(new(32, 32, false, 0, score, [score]), null, Source, candidate, Tool, 1, GatePassed: true, MeasurementLocation: "Worker"));
    private static RemotePerceptualProbeReport ImageReport(RemotePerceptualSearchContract contract) => new(contract.Id,
        contract.Encoder, 80, Source, new(Image: Image(100, Source), ImageMetadata: ImageMetadataResult.Ok(new(false, false))),
        [new(0, 1000, Source, Candidate, Image: Image(90, Candidate), Ssim: ImageQualityResult.Ok(0.95))], EncoderSha256: Tool);

    [Fact]
    public void A_required_source_metadata_read_failure_blocks_before_any_sample_or_baseline_encode()
    {
        var contract = ImageContract();
        var report = ImageReport(contract) with { Source = new(Image: Image(100, Source),
            ImageMetadata: ImageMetadataResult.Failed("Install Compress::Zlib")), Samples = [] };
        var result = RemotePerceptualProbeValidator.Validate(contract, 80, Source, report);
        Assert.True(result.SourceRejected);
        Assert.Contains("Compress::Zlib", result.Error);
    }

    [Fact]
    public void Image_source_and_candidate_must_be_scored_by_the_same_native_binary()
    {
        var contract = ImageContract();
        var report = ImageReport(contract);
        var different = report with { Samples = [report.Samples[0] with { Image = report.Samples[0].Image! with
            { Assessment = report.Samples[0].Image!.Assessment with { MetricSha256 = Candidate } } }] };
        Assert.False(RemotePerceptualProbeValidator.Validate(contract, 80, Source, different).Accepted);
    }

    [Fact]
    public void A_null_native_image_assessment_is_a_source_refusal()
    {
        var contract = ImageContract();
        var report = ImageReport(contract) with { Source = new(Image: Image(100, Source) with { Assessment = null! }) };
        Assert.True(RemotePerceptualProbeValidator.Validate(contract, 80, Source, report).SourceRejected);
    }

    [Fact]
    public void Image_probe_pass_is_recomputed_from_raw_evidence_and_the_frozen_policy()
    {
        var contract = ImageContract();
        var report = ImageReport(contract);
        var accepted = RemotePerceptualProbeValidator.Validate(contract, 80, Source, report);
        Assert.True(accepted.Accepted);
        Assert.True(accepted.Probe!.Measurement.MeetsTarget);
        report = report with { Samples = [report.Samples[0] with { Image = Image(84, Candidate) }] };
        var failed = RemotePerceptualProbeValidator.Validate(contract, 80, Source, report);
        Assert.True(failed.Accepted);
        Assert.True(failed.Probe!.Measurement.Available);
        Assert.False(failed.Probe.Measurement.MeetsTarget);
    }

    [Fact]
    public void Another_contract_encoder_source_setting_or_unrequested_window_cannot_advance_search()
    {
        var contract = ImageContract();
        var report = ImageReport(contract);
        foreach (var changed in new[] { report with { ContractId = Guid.NewGuid() }, report with { Encoder = "mjpeg" },
            report with { SourceSha256 = Candidate }, report with { Value = 60 },
            report with { Samples = [report.Samples[0] with { Index = 1 }] },
            report with { Samples = [report.Samples[0], report.Samples[0]] },
            report with { Samples = [report.Samples[0] with { EncodedBytes = long.MaxValue }] } })
        {
            var refused = RemotePerceptualProbeValidator.Validate(contract, 80, Source, changed);
            Assert.False(refused.Accepted);
            Assert.NotNull(refused.Error);
        }
    }

    [Fact]
    public void Image_source_refusal_cannot_be_converted_to_a_fixed_quality_full_encode()
    {
        var contract = ImageContract();
        var report = ImageReport(contract) with { Source = new(Error: "unsupported colour profile"), Samples = [] };
        var result = RemotePerceptualProbeValidator.Validate(contract, 80, Source, report);
        Assert.False(result.Accepted);
        Assert.True(result.SourceRejected);
        Assert.Contains("unsupported colour", result.Error);
    }

    [Fact]
    public void Saved_ssim_and_alpha_remain_independent_search_targets()
    {
        var contract = ImageContract();
        var report = ImageReport(contract);
        var lowerSsim = report with { Samples = [report.Samples[0] with { Ssim = ImageQualityResult.Ok(0.8) }] };
        Assert.False(RemotePerceptualProbeValidator.Validate(contract, 80, Source, lowerSsim).Probe!.Measurement.MeetsTarget);
        var changedAlpha = report with { Samples = [report.Samples[0] with
        { Image = Image(99, Candidate) with { Assessment = Image(99, Candidate).Assessment with { Measurement = new(32, 32, true, 0.01, 99, [99, 99]) } } }] };
        Assert.False(RemotePerceptualProbeValidator.Validate(contract, 80, Source, changedAlpha).Probe!.Measurement.MeetsTarget);
    }

    [Fact]
    public void Audio_requires_every_requested_window_and_uses_the_worst_channel_and_window()
    {
        var source = new AudioQualityInput(60, 2, 44100, "stereo");
        var contract = new RemotePerceptualSearchContract(Guid.NewGuid(), MediaKind.Audio, "aac", PerceptualQualityPlan.Audio("aac", 128),
            VerificationPolicy.Default with { AudioQualityGateEnabled = true, MaximumAudioQualityDistance = 0.02 },
            PerceptualSamplePlanner.AudioWindows(60), SourceAudio: source);
        RemotePerceptualSampleEvidence Sample(int index, double distance) => new(index, 1000, Reference, Candidate,
            Audio: RemoteAudioQualityEvidence.From(new(true, null, new(30, 2, 48000, "stereo"), new(30, 2, 44100, "stereo"),
                Reference, Candidate, Tool, Tool, Tool, [new(new(0, 30), new(1440000, [0.001, distance]))], 1)));
        var report = new RemotePerceptualProbeReport(contract.Id, "aac", 128, Source, new(Audio: source), [Sample(0, 0.01), Sample(1, 0.03)], EncoderSha256: Tool);
        var result = RemotePerceptualProbeValidator.Validate(contract, 128, Source, report);
        Assert.True(result.Accepted);
        Assert.True(result.Probe!.Measurement.Available);
        Assert.False(result.Probe.Measurement.MeetsTarget);
        Assert.Equal(0.03, result.Probe.Measurement.Score);
        Assert.Equal(2000, result.Probe.EncodedBytes);
        Assert.False(RemotePerceptualProbeValidator.Validate(contract, 128, Source, report with { Samples = [Sample(0, 0.01)] }).Accepted);
    }

    [Fact]
    public void Invalid_measurements_keep_the_unavailable_reason_without_claiming_a_pass()
    {
        var contract = ImageContract();
        var report = ImageReport(contract);
        report = report with { Samples = [report.Samples[0] with { Image = null, Error = "metric timed out" }] };
        var result = RemotePerceptualProbeValidator.Validate(contract, 80, Source, report);
        Assert.True(result.Accepted);
        Assert.False(result.Probe!.Measurement.Available);
        Assert.Contains("metric timed out", result.Probe.Measurement.Error);
    }

    [Fact]
    public void An_older_or_unlabelled_native_image_metric_is_never_relabelled_as_the_current_release()
    {
        var contract = ImageContract();
        var report = ImageReport(contract);
        foreach (var changed in new[] { Image(90, Candidate) with { Revision = "older-build" },
            Image(90, Candidate) with { Metric = "another-score" }, Image(90, Candidate) with { Preparation = "changed-colour-domain" } })
        {
            var result = RemotePerceptualProbeValidator.Validate(contract, 80, Source,
                report with { Samples = [report.Samples[0] with { Image = changed }] });
            Assert.True(result.Accepted);
            Assert.False(result.Probe!.Measurement.Available);
        }
        var source = report with { Source = new(Image: Image(100, Source) with { Revision = "older-build" }) };
        Assert.True(RemotePerceptualProbeValidator.Validate(contract, 80, Source, source).SourceRejected);
    }
}
