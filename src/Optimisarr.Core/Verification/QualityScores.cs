namespace Optimisarr.Core.Verification;

/// <summary>
/// Perceptual/structural quality of an output measured against its original by
/// libvmaf. VMAF is the gate metric; PSNR and SSIM are retained for compatibility
/// with older reports but are not requested by the performance-oriented measurement
/// path, so every field is nullable.
/// </summary>
/// <param name="VmafMean">Arithmetic mean VMAF across frames (0–100).</param>
/// <param name="VmafHarmonicMean">Harmonic-mean VMAF — penalises bad frames more than the plain mean.</param>
/// <param name="VmafMin">The lowest single-frame VMAF, used to catch short artifact bursts.</param>
/// <param name="PsnrYMean">Mean luma PSNR in dB, when computed.</param>
/// <param name="SsimMean">Mean SSIM (0–1), when computed.</param>
/// <param name="ModelVersion">The libvmaf model selected for the source resolution.</param>
/// <param name="Preprocessing">The colour-domain preparation applied before measurement.</param>
public sealed record QualityScores(
    double? VmafMean,
    double? VmafHarmonicMean,
    double? VmafMin,
    double? PsnrYMean,
    double? SsimMean,
    string? ModelVersion = null,
    string? Preprocessing = null);
