using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

public sealed class VerificationEvaluatorTests
{
    // A converted output that passes every default gate: decodes cleanly, probes,
    // keeps duration and audio, and is meaningfully smaller than the original.
    private static VerificationInput Healthy() => new(
        DecodeSucceeded: true,
        DecodeError: null,
        DecodeErrorCount: 0,
        OutputProbeSucceeded: true,
        OutputProbeError: null,
        OutputVideoCodec: "hevc",
        OriginalSizeBytes: 1_000_000_000,
        OutputSizeBytes: 600_000_000,
        OriginalDurationSeconds: 3600,
        OutputDurationSeconds: 3600,
        OriginalAudioTrackCount: 2,
        OutputAudioTrackCount: 2,
        OriginalSubtitleTrackCount: 1,
        OutputSubtitleTrackCount: 1);

    private static CheckOutcome Outcome(VerificationReport report, string name) =>
        report.Checks.Single(check => check.Name == name).Outcome;

    [Fact]
    public void A_healthy_output_passes_every_check()
    {
        var report = VerificationEvaluator.Evaluate(Healthy(), VerificationPolicy.Default);

        Assert.True(report.Passed);
        Assert.All(report.Checks, check => Assert.Equal(CheckOutcome.Passed, check.Outcome));
    }

    [Fact]
    public void Decode_failure_fails_verification()
    {
        var input = Healthy() with { DecodeSucceeded = false, DecodeError = "corrupt frame" };

        var report = VerificationEvaluator.Evaluate(input, VerificationPolicy.Default);

        Assert.False(report.Passed);
        Assert.Equal(CheckOutcome.Failed, Outcome(report, "Decode health"));
    }

    [Fact]
    public void Unreadable_output_fails_verification()
    {
        var input = Healthy() with { OutputProbeSucceeded = false, OutputProbeError = "invalid data" };

        var report = VerificationEvaluator.Evaluate(input, VerificationPolicy.Default);

        Assert.Equal(CheckOutcome.Failed, Outcome(report, "Output readable"));
        Assert.False(report.Passed);
    }

    [Fact]
    public void Missing_video_stream_fails_verification()
    {
        var input = Healthy() with { OutputVideoCodec = null };

        var report = VerificationEvaluator.Evaluate(input, VerificationPolicy.Default);

        Assert.Equal(CheckOutcome.Failed, Outcome(report, "Video stream"));
    }

    [Fact]
    public void Duration_drift_within_tolerance_passes()
    {
        // 18s of 3600s is 0.5%, under the 1% default tolerance.
        var input = Healthy() with { OutputDurationSeconds = 3582 };

        var report = VerificationEvaluator.Evaluate(input, VerificationPolicy.Default);

        Assert.Equal(CheckOutcome.Passed, Outcome(report, "Duration"));
    }

    [Fact]
    public void Duration_drift_beyond_tolerance_fails()
    {
        // 90s of 3600s is 2.5%, over the 1% default tolerance.
        var input = Healthy() with { OutputDurationSeconds = 3510 };

        var report = VerificationEvaluator.Evaluate(input, VerificationPolicy.Default);

        Assert.Equal(CheckOutcome.Failed, Outcome(report, "Duration"));
    }

    [Fact]
    public void Unknown_durations_fail_the_duration_check()
    {
        var noOriginal = Healthy() with { OriginalDurationSeconds = null };
        var noOutput = Healthy() with { OutputDurationSeconds = null };

        Assert.Equal(CheckOutcome.Failed, Outcome(
            VerificationEvaluator.Evaluate(noOriginal, VerificationPolicy.Default), "Duration"));
        Assert.Equal(CheckOutcome.Failed, Outcome(
            VerificationEvaluator.Evaluate(noOutput, VerificationPolicy.Default), "Duration"));
    }

    [Fact]
    public void Lost_audio_track_fails_when_retention_is_required()
    {
        var input = Healthy() with { OutputAudioTrackCount = 1 };

        var report = VerificationEvaluator.Evaluate(input, VerificationPolicy.Default);

        Assert.Equal(CheckOutcome.Failed, Outcome(report, "Audio tracks"));
    }

    [Fact]
    public void Lost_audio_track_passes_when_retention_is_not_required()
    {
        var input = Healthy() with { OutputAudioTrackCount = 1 };
        var policy = VerificationPolicy.Default with { RequireAudioRetained = false };

        var report = VerificationEvaluator.Evaluate(input, policy);

        Assert.Equal(CheckOutcome.Passed, Outcome(report, "Audio tracks"));
    }

    [Fact]
    public void Lost_subtitles_pass_by_default_but_fail_when_required()
    {
        var input = Healthy() with { OutputSubtitleTrackCount = 0 };

        Assert.Equal(CheckOutcome.Passed, Outcome(
            VerificationEvaluator.Evaluate(input, VerificationPolicy.Default), "Subtitle tracks"));
        Assert.Equal(CheckOutcome.Failed, Outcome(
            VerificationEvaluator.Evaluate(input, VerificationPolicy.Default with { RequireSubtitlesRetained = true }),
            "Subtitle tracks"));
    }

    [Fact]
    public void Output_that_is_not_smaller_fails_the_size_check()
    {
        var input = Healthy() with { OutputSizeBytes = 1_200_000_000 };

        var report = VerificationEvaluator.Evaluate(input, VerificationPolicy.Default);

        Assert.Equal(CheckOutcome.Failed, Outcome(report, "Size saving"));
    }

    [Fact]
    public void Equal_size_fails_when_reduction_required_but_passes_when_not()
    {
        var input = Healthy() with { OutputSizeBytes = 1_000_000_000 };

        Assert.Equal(CheckOutcome.Failed, Outcome(
            VerificationEvaluator.Evaluate(input, VerificationPolicy.Default), "Size saving"));
        Assert.Equal(CheckOutcome.Passed, Outcome(
            VerificationEvaluator.Evaluate(input, VerificationPolicy.Default with { RequireSizeReduction = false }),
            "Size saving"));
    }

    [Fact]
    public void Empty_output_fails_the_size_check()
    {
        var input = Healthy() with { OutputSizeBytes = 0 };

        var report = VerificationEvaluator.Evaluate(input, VerificationPolicy.Default);

        Assert.Equal(CheckOutcome.Failed, Outcome(report, "Size saving"));
    }

    private const string HdrCheck = "HDR signal";

    [Fact]
    public void Hdr_check_is_absent_for_an_sdr_original()
    {
        var report = VerificationEvaluator.Evaluate(Healthy(), VerificationPolicy.Default);

        Assert.DoesNotContain(report.Checks, check => check.Name == HdrCheck);
    }

    [Fact]
    public void Hdr_original_that_keeps_its_hdr_signal_passes()
    {
        var input = Healthy() with { OriginalIsHdr = true, OutputIsHdr = true };

        var report = VerificationEvaluator.Evaluate(input, VerificationPolicy.Default);

        Assert.True(report.Passed);
        Assert.Equal(CheckOutcome.Passed, Outcome(report, HdrCheck));
    }

    [Fact]
    public void Hdr_original_that_silently_loses_hdr_fails()
    {
        var input = Healthy() with { OriginalIsHdr = true, OutputIsHdr = false };

        var report = VerificationEvaluator.Evaluate(input, VerificationPolicy.Default);

        Assert.False(report.Passed);
        Assert.Equal(CheckOutcome.Failed, Outcome(report, HdrCheck));
    }

    [Fact]
    public void Hdr_original_intentionally_tone_mapped_to_sdr_passes()
    {
        var input = Healthy() with { OriginalIsHdr = true, OutputIsHdr = false, HdrConvertedToSdr = true };

        var report = VerificationEvaluator.Evaluate(input, VerificationPolicy.Default);

        Assert.Equal(CheckOutcome.Passed, Outcome(report, HdrCheck));
    }

    private static readonly VerificationPolicy QualityGate =
        VerificationPolicy.Default with { QualityGateEnabled = true };

    private const string QualityCheck = "Perceptual quality (VMAF)";

    [Fact]
    public void Quality_gate_is_absent_unless_enabled()
    {
        var report = VerificationEvaluator.Evaluate(Healthy(), VerificationPolicy.Default);

        Assert.DoesNotContain(report.Checks, check => check.Name == QualityCheck);
    }

    [Fact]
    public void Quality_gate_passes_when_vmaf_clears_both_floors()
    {
        var input = Healthy() with
        {
            QualityMeasured = true,
            QualityScores = new QualityScores(95.0, 94.5, 88.0, 45.0, 0.99)
        };

        var report = VerificationEvaluator.Evaluate(input, QualityGate);

        Assert.True(report.Passed);
        Assert.Equal(CheckOutcome.Passed, Outcome(report, QualityCheck));
    }

    [Fact]
    public void Quality_gate_fails_when_a_bad_frame_drops_below_the_min_floor()
    {
        // Healthy harmonic mean, but one stretch of frames falls to 70 — under the 80 floor.
        var input = Healthy() with
        {
            QualityMeasured = true,
            QualityScores = new QualityScores(94.0, 93.5, 70.0, 44.0, 0.98)
        };

        var report = VerificationEvaluator.Evaluate(input, QualityGate);

        Assert.Equal(CheckOutcome.Failed, Outcome(report, QualityCheck));
    }

    [Fact]
    public void Quality_gate_fails_when_the_harmonic_mean_is_too_low()
    {
        var input = Healthy() with
        {
            QualityMeasured = true,
            QualityScores = new QualityScores(91.0, 90.0, 85.0, 42.0, 0.97)
        };

        var report = VerificationEvaluator.Evaluate(input, QualityGate);

        Assert.Equal(CheckOutcome.Failed, Outcome(report, QualityCheck));
    }

    [Fact]
    public void Quality_gate_fails_closed_when_quality_could_not_be_measured()
    {
        var input = Healthy() with
        {
            QualityMeasured = false,
            QualityError = "libvmaf not available"
        };

        var report = VerificationEvaluator.Evaluate(input, QualityGate);

        Assert.Equal(CheckOutcome.Failed, Outcome(report, QualityCheck));
    }
}
