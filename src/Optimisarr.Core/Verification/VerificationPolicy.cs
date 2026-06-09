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
    double MaxLoudnessDriftLufs)
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
        MaxLoudnessDriftLufs: 1.0);
}
