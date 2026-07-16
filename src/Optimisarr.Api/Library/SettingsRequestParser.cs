using Optimisarr.Core.Queue;
using Optimisarr.Core.Verification;

namespace Optimisarr.Api.Library;

internal sealed record SettingsRequestError(string Code, string Message);

internal static class SettingsRequestParser
{
    public static bool TryParse(
        SettingsDto request,
        out QueueSettings settings,
        out SettingsRequestError? error)
    {
        settings = default!;

        if (request.MaxConcurrentJobs < 1)
            return Fail("settings.maxConcurrentJobs.minimum", "Max concurrent jobs must be at least 1.", out error);
        if (request.MinFreeDiskBytes < 0)
            return Fail("settings.minFreeDiskBytes.nonNegative", "Minimum free disk space cannot be negative.", out error);
        if (request.CpuThreadLimit < 0)
            return Fail("settings.cpuThreadLimit.nonNegative", "CPU thread limit cannot be negative.", out error);
        if (request.LibraryScanIntervalHours < 1)
            return Fail("settings.libraryScanIntervalHours.minimum", "Library scan interval must be at least 1 hour.", out error);
        if (request.VerificationDurationTolerancePercent < 0)
            return Fail("settings.verificationDurationTolerance.nonNegative", "Verification duration tolerance cannot be negative.", out error);
        if (request.VerificationMaxLoudnessDriftLufs < 0)
            return Fail("settings.loudnessDrift.nonNegative", "Loudness drift tolerance cannot be negative.", out error);
        if (!double.IsFinite(request.VerificationMaxTruePeakDbtp))
            return Fail("settings.truePeak.finite", "True-peak ceiling must be a finite dBTP value.", out error);
        if (request.VerificationMinimumImageSsim is < 0 or > 1)
            return Fail("settings.imageSsim.range", "Image SSIM threshold must be between 0 and 1.", out error);
        if (request.ReplacementQuarantineRetentionDays < 0)
            return Fail("settings.quarantineRetention.nonNegative", "Quarantine retention days cannot be negative.", out error);
        if (!Enum.TryParse<EncoderMode>(request.EncoderMode, ignoreCase: true, out var encoderMode))
            return Fail("settings.encoderMode.invalid", "Encoder mode must be one of Auto, Cpu, NvidiaNvenc, IntelQsv, or Vaapi.", out error);

        settings = new QueueSettings(
            request.MaxConcurrentJobs,
            request.MinFreeDiskBytes,
            request.CpuThreadLimit,
            request.LibraryScanIntervalHours,
            encoderMode,
            request.HardwareDecode,
            new VerificationPolicy(
                request.VerificationDurationTolerancePercent,
                request.VerificationRequireAudioRetained,
                request.VerificationRequireSubtitlesRetained,
                request.VerificationRequireSizeReduction,
                VerificationPolicy.Default.QualityGateEnabled,
                VerificationPolicy.Default.MinimumVmafHarmonicMean,
                VerificationPolicy.Default.MinimumVmafMin,
                VerificationPolicy.Default.MinimumVmafCatastrophicMin,
                request.VerificationAudioLoudnessGateEnabled,
                request.VerificationMaxLoudnessDriftLufs,
                request.VerificationAudioClippingGateEnabled,
                request.VerificationMaxTruePeakDbtp,
                request.VerificationImageQualityGateEnabled,
                request.VerificationMinimumImageSsim,
                request.VerificationImageMetadataGateEnabled,
                VerificationPolicy.Default.ClipVmafEnabled,
                VerificationPolicy.Default.VmafFrameSubsample),
            request.ReplacementAllowCrossFilesystem,
            request.DryRunMode,
            request.ReplacementQuarantineRetentionDays);
        error = null;
        return true;
    }

    private static bool Fail(string code, string message, out SettingsRequestError? error)
    {
        error = new SettingsRequestError(code, message);
        return false;
    }
}
