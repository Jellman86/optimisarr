using Optimisarr.Core.Queue;
using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

public sealed class HardwareDecodeFallbackTests
{
    // --- After verification: the corruption signature ------------------------------------------
    //
    // Intel QSV decoded an H.264 MKV into broken frames on two hosts; the same encoder fed by
    // software decode scored 96. The gates rejected every corrupt output, so nothing was harmed,
    // but each cost a full encode, then a higher-quality retry, then a permanent auto-exclusion.

    private const double Floor = 40;

    private static VerificationReport Report(
        double? lowestFrame,
        bool measured = true,
        params (string Name, CheckOutcome Outcome)[] checks) =>
        new(
            checks.Select(check => new VerificationCheck(check.Name, check.Outcome, "")).ToList(),
            Vmaf: new VmafEvidence(
                measured,
                null,
                measured
                    ? new QualityScores(VmafMean: 58, VmafHarmonicMean: 6, VmafMin: lowestFrame, PsnrYMean: null, SsimMean: null)
                    : null));

    [Fact]
    public void Frames_scoring_below_the_catastrophic_floor_are_read_as_a_corrupt_decode()
    {
        // The observed shape: 159 of 959 frames at zero, harmonic mean in single digits.
        var report = Report(lowestFrame: 0, checks: [("Perceptual quality (VMAF)", CheckOutcome.Failed)]);

        Assert.True(HardwareDecodeFallback.ShouldRetryAfterVerification(report, Floor));
    }

    [Fact]
    public void Decode_errors_in_the_output_are_read_as_a_corrupt_decode_whatever_else_failed()
    {
        var report = Report(
            lowestFrame: 0,
            checks:
            [
                ("Decode health", CheckOutcome.Failed),
                ("Size saving", CheckOutcome.Failed),
                ("Perceptual quality (VMAF)", CheckOutcome.Failed)
            ]);

        Assert.True(HardwareDecodeFallback.ShouldRetryAfterVerification(report, Floor));
    }

    [Fact]
    public void A_weak_but_intact_encode_keeps_the_higher_quality_retry()
    {
        // Worst frame above the floor: the encoder simply did not spend enough. That is what the
        // higher-quality retry is for, and a software re-encode would change nothing.
        var report = Report(lowestFrame: 62, checks: [("Perceptual quality (VMAF)", CheckOutcome.Failed)]);

        Assert.False(HardwareDecodeFallback.ShouldRetryAfterVerification(report, Floor));
    }

    [Fact]
    public void A_size_saving_failure_alone_is_not_a_decode_problem()
    {
        var report = Report(lowestFrame: 91, checks: [("Size saving", CheckOutcome.Failed)]);

        Assert.False(HardwareDecodeFallback.ShouldRetryAfterVerification(report, Floor));
    }

    [Fact]
    public void An_unmeasured_vmaf_gives_no_grounds_to_retry()
    {
        var report = Report(lowestFrame: null, measured: false, checks: [("Perceptual quality (VMAF)", CheckOutcome.Failed)]);

        Assert.False(HardwareDecodeFallback.ShouldRetryAfterVerification(report, Floor));
    }

    [Fact]
    public void A_passing_report_never_retries()
    {
        var report = Report(lowestFrame: 0, checks: [("Perceptual quality (VMAF)", CheckOutcome.Passed)]);

        Assert.False(HardwareDecodeFallback.ShouldRetryAfterVerification(report, Floor));
    }

    // --- Before verification: ffmpeg setup failures (unchanged) --------------------------------

    [Theory]
    [InlineData("[hevc_qsv @ 0x..] Failed setup for format qsv: hwaccel initialisation returned error")]
    [InlineData("Error while opening decoder for input stream")]
    [InlineData("Impossible to convert between the formats supported by the filter")]
    [InlineData("[AVHWDeviceContext] Device creation failed: -22")]
    [InlineData("No device available for decoder: device type cuda needed for codec h264")]
    [InlineData("Device setup failed for decoder on input stream #0:0 : Operation not permitted")]
    public void Retries_in_software_for_a_hardware_decode_failure(string stderr)
    {
        Assert.True(HardwareDecodeFallback.ShouldRetryInSoftware(stderr));
    }

    [Theory]
    [InlineData("av_interleaved_write_frame(): No space left on device")]
    [InlineData("Conversion failed!")]
    [InlineData("Output file is empty, nothing was encoded")]
    public void Does_not_retry_for_an_unrelated_failure(string stderr)
    {
        Assert.False(HardwareDecodeFallback.ShouldRetryInSoftware(stderr));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Does_not_retry_when_there_is_no_error_text(string? stderr)
    {
        Assert.False(HardwareDecodeFallback.ShouldRetryInSoftware(stderr));
    }

    [Fact]
    public void Matching_is_case_insensitive()
    {
        Assert.True(HardwareDecodeFallback.ShouldRetryInSoftware("FAILED SETUP FOR FORMAT QSV"));
    }
}
