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

    private const string AudioFidelityCheck = "Audio fidelity";

    [Fact]
    public void Audio_fidelity_is_absent_when_the_original_audio_shape_is_unknown()
    {
        var report = VerificationEvaluator.Evaluate(Healthy(), VerificationPolicy.Default);

        Assert.DoesNotContain(report.Checks, check => check.Name == AudioFidelityCheck);
    }

    [Fact]
    public void Retained_channels_and_sample_rate_pass_audio_fidelity()
    {
        var input = Healthy() with
        {
            OriginalMaxAudioChannels = 6, OutputMaxAudioChannels = 6,
            OriginalMaxAudioSampleRate = 48000, OutputMaxAudioSampleRate = 48000
        };

        var report = VerificationEvaluator.Evaluate(input, VerificationPolicy.Default);

        Assert.True(report.Passed);
        Assert.Equal(CheckOutcome.Passed, Outcome(report, AudioFidelityCheck));
    }

    [Fact]
    public void A_silent_downmix_fails_audio_fidelity()
    {
        var input = Healthy() with { OriginalMaxAudioChannels = 6, OutputMaxAudioChannels = 2 };

        var report = VerificationEvaluator.Evaluate(input, VerificationPolicy.Default);

        Assert.Equal(CheckOutcome.Failed, Outcome(report, AudioFidelityCheck));
    }

    [Fact]
    public void A_sample_rate_drop_fails_audio_fidelity()
    {
        var input = Healthy() with
        {
            OriginalMaxAudioChannels = 2, OutputMaxAudioChannels = 2,
            OriginalMaxAudioSampleRate = 48000, OutputMaxAudioSampleRate = 44100
        };

        var report = VerificationEvaluator.Evaluate(input, VerificationPolicy.Default);

        Assert.Equal(CheckOutcome.Failed, Outcome(report, AudioFidelityCheck));
    }

    [Fact]
    public void Audio_fidelity_is_skipped_when_retention_is_not_required()
    {
        var input = Healthy() with { OriginalMaxAudioChannels = 6, OutputMaxAudioChannels = 2 };
        var policy = VerificationPolicy.Default with { RequireAudioRetained = false };

        var report = VerificationEvaluator.Evaluate(input, policy);

        Assert.DoesNotContain(report.Checks, check => check.Name == AudioFidelityCheck);
    }

    private const string ColorCheck = "Colour metadata";
    private const string SyncCheck = "A/V sync";

    [Fact]
    public void Colour_metadata_check_is_absent_when_the_original_declares_none()
    {
        var report = VerificationEvaluator.Evaluate(Healthy(), VerificationPolicy.Default);

        Assert.DoesNotContain(report.Checks, check => check.Name == ColorCheck);
    }

    [Fact]
    public void Preserved_colour_metadata_passes()
    {
        var input = Healthy() with
        {
            OriginalColorPrimaries = "bt709", OutputColorPrimaries = "bt709",
            OriginalColorTransfer = "bt709", OutputColorTransfer = "bt709",
            OriginalColorSpace = "bt709", OutputColorSpace = "bt709"
        };

        var report = VerificationEvaluator.Evaluate(input, VerificationPolicy.Default);

        Assert.True(report.Passed);
        Assert.Equal(CheckOutcome.Passed, Outcome(report, ColorCheck));
    }

    [Fact]
    public void A_definite_colour_mismatch_fails()
    {
        var input = Healthy() with { OriginalColorPrimaries = "bt709", OutputColorPrimaries = "bt601" };

        var report = VerificationEvaluator.Evaluate(input, VerificationPolicy.Default);

        Assert.Equal(CheckOutcome.Failed, Outcome(report, ColorCheck));
    }

    [Fact]
    public void A_dropped_colour_tag_on_the_output_is_treated_as_benign()
    {
        var input = Healthy() with { OriginalColorPrimaries = "bt709", OutputColorPrimaries = null };

        var report = VerificationEvaluator.Evaluate(input, VerificationPolicy.Default);

        Assert.Equal(CheckOutcome.Passed, Outcome(report, ColorCheck));
    }

    [Fact]
    public void Aligned_audio_and_video_starts_pass_sync()
    {
        var input = Healthy() with { OutputVideoStartSeconds = 0.0, OutputAudioStartSeconds = 0.02 };

        var report = VerificationEvaluator.Evaluate(input, VerificationPolicy.Default);

        Assert.Equal(CheckOutcome.Passed, Outcome(report, SyncCheck));
    }

    [Fact]
    public void Gross_av_desync_fails()
    {
        var input = Healthy() with { OutputVideoStartSeconds = 0.0, OutputAudioStartSeconds = 1.5 };

        var report = VerificationEvaluator.Evaluate(input, VerificationPolicy.Default);

        Assert.Equal(CheckOutcome.Failed, Outcome(report, SyncCheck));
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

    private static readonly VerificationPolicy LoudnessGate =
        VerificationPolicy.Default with { AudioLoudnessGateEnabled = true, MaxLoudnessDriftLufs = 1.0 };

    private const string LoudnessCheck = "Audio loudness (EBU R128)";

    [Fact]
    public void Loudness_gate_is_absent_unless_enabled()
    {
        var report = VerificationEvaluator.Evaluate(Healthy(), VerificationPolicy.Default);

        Assert.DoesNotContain(report.Checks, check => check.Name == LoudnessCheck);
    }

    [Fact]
    public void Loudness_within_tolerance_passes()
    {
        var input = Healthy() with
        {
            LoudnessMeasured = true, OriginalLoudnessLufs = -23.0, OutputLoudnessLufs = -23.4
        };

        var report = VerificationEvaluator.Evaluate(input, LoudnessGate);

        Assert.True(report.Passed);
        Assert.Equal(CheckOutcome.Passed, Outcome(report, LoudnessCheck));
    }

    [Fact]
    public void Loudness_drift_beyond_tolerance_fails()
    {
        var input = Healthy() with
        {
            LoudnessMeasured = true, OriginalLoudnessLufs = -23.0, OutputLoudnessLufs = -19.0
        };

        var report = VerificationEvaluator.Evaluate(input, LoudnessGate);

        Assert.Equal(CheckOutcome.Failed, Outcome(report, LoudnessCheck));
    }

    [Fact]
    public void Loudness_gate_fails_closed_when_not_measured()
    {
        var input = Healthy() with { LoudnessMeasured = false, LoudnessError = "no audio" };

        var report = VerificationEvaluator.Evaluate(input, LoudnessGate);

        Assert.Equal(CheckOutcome.Failed, Outcome(report, LoudnessCheck));
    }
}
