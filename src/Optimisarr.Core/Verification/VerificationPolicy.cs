using Optimisarr.Core.Domain;

namespace Optimisarr.Core.Verification;

/// <summary>
/// The thresholds a converted output must satisfy before a job may move toward
/// replacement. Defaults are conservative: the output must decode cleanly, keep
/// the same duration (within tolerance), retain every audio track, and be smaller
/// than the original. Subtitle retention is not required by default because some
/// rule profiles intentionally drop image-based subtitles.
///
/// The perceptual-quality (VMAF) gate is off by default: measuring it fully decodes
/// both files and scores every frame, which roughly doubles verification time and can
/// dominate a run on modest hardware. An operator opts in (via the quality slider in
/// Settings) when the extra safeguard is worth the cost; the structural, duration and
/// size gates plus quarantine rollback still protect every replacement while it is off.
/// It is skipped for remuxes and non-video media, where VMAF has no useful work to do.
/// When enabled, the output must clear a harmonic-mean VMAF floor, a fifth-percentile
/// floor for sustained difficult content, and a lower single-frame catastrophic floor.
///
/// The audio loudness and clipping gates are opt-in too and share one
/// <c>ebur128</c> decode pass: the loudness gate bounds EBU R128 drift, and the
/// clipping gate fails an output whose true peak rises above the ceiling when the
/// original sat below it — i.e. the re-encode introduced clipping.
///
/// The image-quality gate is the still-image counterpart of VMAF: it is on by default
/// (measuring it runs ffmpeg's <c>ssim</c> filter over the original and output
/// pictures) and, when enabled, blocks replacement when the structural similarity
/// of the re-encoded still falls below a floor. SSIM (not VMAF) is the image metric
/// because VMAF is tuned for moving video, while SSIM is a well-understood
/// structural measure for a single frame.
///
/// The image-metadata gate is also on by default: when enabled it fails an image whose
/// re-encode silently dropped the source's embedded ICC colour profile or its EXIF
/// metadata. Some encoders/containers discard these by default, which can shift
/// colours or lose capture data, so a colour-sensitive library can demand they
/// survive. The gate only flags *loss* — an output may gain metadata (Optimisarr
/// stamps its own marker) without failing.
/// </summary>
public sealed record VerificationPolicy(
    double DurationTolerancePercent,
    bool RequireAudioRetained,
    bool RequireSubtitlesRetained,
    bool RequireSizeReduction,
    bool QualityGateEnabled,
    double MinimumVmafHarmonicMean,
    double MinimumVmafMin,
    double MinimumVmafCatastrophicMin,
    bool AudioLoudnessGateEnabled,
    double MaxLoudnessDriftLufs,
    bool AudioClippingGateEnabled,
    double MaxTruePeakDbtp,
    bool ImageQualityGateEnabled,
    double MinimumImageSsim,
    bool ImageMetadataGateEnabled = false,
    bool ClipVmafEnabled = false,
    int VmafFrameSubsample = 1,
    bool MeasureVmaf = false)
{
    public static VerificationPolicy Default { get; } = new(
        DurationTolerancePercent: 1.0,
        RequireAudioRetained: true,
        RequireSubtitlesRetained: false,
        RequireSizeReduction: true,
        QualityGateEnabled: false,
        MinimumVmafHarmonicMean: 93.0,
        MinimumVmafMin: 80.0,
        MinimumVmafCatastrophicMin: 50.0,
        AudioLoudnessGateEnabled: false,
        MaxLoudnessDriftLufs: 1.0,
        AudioClippingGateEnabled: false,
        MaxTruePeakDbtp: 0.0,
        ImageQualityGateEnabled: true,
        MinimumImageSsim: 0.95,
        ImageMetadataGateEnabled: true,
        ClipVmafEnabled: false,
        VmafFrameSubsample: 1);

    public bool RequiresVmaf(MediaKind kind, bool videoReencoded) =>
        (QualityGateEnabled || MeasureVmaf) && kind == MediaKind.Video && videoReencoded;
}
