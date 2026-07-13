using Optimisarr.Core.Domain;

namespace Optimisarr.Core.Verification;

/// <summary>
/// The thresholds a converted output must satisfy before a job may move toward
/// replacement. Defaults are conservative: the output must decode cleanly, keep
/// the same duration (within tolerance), retain every audio track, and be smaller
/// than the original. Subtitle retention is not required by default because some
/// rule profiles intentionally drop image-based subtitles.
///
/// The perceptual-quality (VMAF) gate is on by default for video re-encodes because
/// structural checks cannot prove that the picture still looks acceptable. It is
/// skipped for remuxes and non-video media, where VMAF has no useful work to do.
/// Measuring it roughly doubles verification time; an operator may explicitly opt
/// out when throughput matters more than the additional quality safeguard. When
/// enabled, the output must clear both a harmonic-mean VMAF floor (which penalises
/// bad frames) and a per-frame minimum floor (which catches short artifact bursts
/// a healthy average would hide).
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
///
/// The image-metadata gate is also opt-in: when enabled it fails an image whose
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
    bool AudioLoudnessGateEnabled,
    double MaxLoudnessDriftLufs,
    bool AudioClippingGateEnabled,
    double MaxTruePeakDbtp,
    bool ImageQualityGateEnabled,
    double MinimumImageSsim,
    bool ImageMetadataGateEnabled = false)
{
    public static VerificationPolicy Default { get; } = new(
        DurationTolerancePercent: 1.0,
        RequireAudioRetained: true,
        RequireSubtitlesRetained: false,
        RequireSizeReduction: true,
        QualityGateEnabled: true,
        MinimumVmafHarmonicMean: 93.0,
        MinimumVmafMin: 80.0,
        AudioLoudnessGateEnabled: false,
        MaxLoudnessDriftLufs: 1.0,
        AudioClippingGateEnabled: false,
        MaxTruePeakDbtp: 0.0,
        ImageQualityGateEnabled: false,
        MinimumImageSsim: 0.95,
        ImageMetadataGateEnabled: false);

    public bool RequiresVmaf(MediaKind kind, bool videoReencoded) =>
        QualityGateEnabled && kind == MediaKind.Video && videoReencoded;
}
