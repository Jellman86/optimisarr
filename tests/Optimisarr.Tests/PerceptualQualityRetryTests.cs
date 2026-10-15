using Optimisarr.Core.Domain;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

public sealed class PerceptualQualityRetryTests
{
    private static readonly string Hash = new('a', 64);
    private static readonly VerificationPolicy Policy = VerificationPolicy.Default with
        { ImagePerceptualGateEnabled = true, MinimumImagePerceptualScore = 85, AudioQualityGateEnabled = true, MaximumAudioQualityDistance = 0.01 };

    [Fact]
    public void Sole_measured_image_quality_failure_permits_one_higher_fidelity_retry()
    {
        var plan = PerceptualQualityPlan.Image("libwebp", 80);
        var next = PerceptualQualityRetryPolicy.Next(MediaKind.Image, plan, 60, 0, Image(), Policy);
        Assert.True(next > 60);
        Assert.Null(PerceptualQualityRetryPolicy.Next(MediaKind.Image, plan, 60, 1, Image(), Policy));
        Assert.Null(PerceptualQualityRetryPolicy.Next(MediaKind.Image, plan, 100, 0, Image(), Policy));
    }

    [Fact]
    public void Sole_measured_audio_quality_failure_retries_at_the_next_codec_bitrate()
    {
        var next = PerceptualQualityRetryPolicy.Next(MediaKind.Audio, PerceptualQualityPlan.Audio("aac", 128), 96, 0, Audio(), Policy);
        Assert.Equal(112, next);
    }

    [Theory]
    [InlineData("Size reduction")]
    [InlineData("Decode health")]
    [InlineData("Duration")]
    [InlineData("Image metadata")]
    [InlineData("Audio clipping")]
    public void Any_other_failed_gate_blocks_quality_retry(string failed)
    {
        var report = Image() with { Checks = [.. Image().Checks, new(failed, CheckOutcome.Failed, "failed")] };
        Assert.Null(PerceptualQualityRetryPolicy.Next(MediaKind.Image, PerceptualQualityPlan.Image("mjpeg", 80), 60, 0, report, Policy));
    }

    [Fact]
    public void Missing_metric_or_alpha_damage_never_triggers_a_quality_retry()
    {
        var report = Image();
        var plan = PerceptualQualityPlan.Image("libwebp", 80);
        Assert.Null(PerceptualQualityRetryPolicy.Next(MediaKind.Image, plan, 60, 0, report with { ImagePerceptualQuality = null }, Policy));
        Assert.Null(PerceptualQualityRetryPolicy.Next(MediaKind.Image, plan, 60, 0,
            report with { ImagePerceptualQuality = report.ImagePerceptualQuality! with { Measurement = new(32, 32, true, 0.01, 80, [80, 80]) } }, Policy));
        Assert.Null(PerceptualQualityRetryPolicy.Next(MediaKind.Audio, PerceptualQualityPlan.Audio("aac", 128), 96, 0,
            Audio() with { AudioQuality = null }, Policy));
    }

    private static VerificationReport Image() => new([new(ImagePerceptualQualityGate.CheckName, CheckOutcome.Failed, "below target")],
        ImagePerceptualQuality: new(new(32, 32, false, 0, 80, [80]), null, Hash, Hash, Hash, 1));

    private static VerificationReport Audio() => new([new(AudioQualityGate.CheckName, CheckOutcome.Failed, "above limit")],
        AudioQuality: new("Server", RemoteAudioQualityEvidence.From(new(true, null,
            new(30, 2, 48000, "stereo"), new(30, 2, 48000, "stereo"), Hash, Hash, Hash, Hash, Hash,
            [new(new(0, 30), new(1440000, [0.005, 0.02]))], 1)), null));
}
