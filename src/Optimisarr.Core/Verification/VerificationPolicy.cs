namespace Optimisarr.Core.Verification;

/// <summary>
/// The thresholds a converted output must satisfy before a job may move toward
/// replacement. Defaults are conservative: the output must decode cleanly, keep
/// the same duration (within tolerance), retain every audio track, and be smaller
/// than the original. Subtitle retention is not required by default because some
/// rule profiles intentionally drop image-based subtitles.
///
/// The perceptual-quality (VMAF) gate is opt-in: measuring it requires an ffmpeg
/// built with libvmaf and roughly doubles verification time, so it is off by
/// default. When enabled, the output must clear both a harmonic-mean VMAF floor
/// (which penalises bad frames) and a per-frame minimum floor (which catches short
/// artifact bursts a healthy average would hide).
///
/// The audio loudness and clipping gates are opt-in too and share one
/// <c>ebur128</c> decode pass: the loudness gate bounds EBU R128 drift, and the
/// clipping gate fails an output whose true peak rises above the ceiling when the
/// original sat below it — i.e. the re-encode introduced clipping.
///
/// The image-quality gate is the still-image counterpart of VMAF: it is opt-in
/// (measuring it runs ffmpeg's <c>ssim</c> filter over the original and output
/// pictures) and, when enabled, blocks replacement when the structural similarity
/// of the re-encoded still falls below a floor. SSIM (not VMAF) is the image metric
/// because VMAF is tuned for moving video, while SSIM is a well-understood
/// structural measure for a single frame.
/// </summary>
public sealed record VerificationPolicy(
    double DurationTolerancePercent,
    bool RequireAudioRetained,
    bool RequireSubtitlesRetained,
    bool RequireSizeReduction,
    bool QualityGateEnabled,
    double MinimumVmafHarmonicMean,
    double MinimumVmafMin,
    bool AudioLoudnessGateEnabled,
    double MaxLoudnessDriftLufs,
    bool AudioClippingGateEnabled,
    double MaxTruePeakDbtp,
    bool ImageQualityGateEnabled,
    double MinimumImageSsim)
{
    public static VerificationPolicy Default { get; } = new(
        DurationTolerancePercent: 1.0,
        RequireAudioRetained: true,
        RequireSubtitlesRetained: false,
        RequireSizeReduction: true,
        QualityGateEnabled: false,
        MinimumVmafHarmonicMean: 93.0,
        MinimumVmafMin: 80.0,
        AudioLoudnessGateEnabled: false,
        MaxLoudnessDriftLufs: 1.0,
        AudioClippingGateEnabled: false,
        MaxTruePeakDbtp: 0.0,
        ImageQualityGateEnabled: false,
        MinimumImageSsim: 0.95);
}
