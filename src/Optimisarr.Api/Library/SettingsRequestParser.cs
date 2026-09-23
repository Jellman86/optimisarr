using Optimisarr.Api.Workers;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Verification;

namespace Optimisarr.Api.Library;

internal sealed record SettingsRequestError(string Code, string Message);

internal static class SettingsRequestParser
{
    public static bool TryParse(
        SettingsDto request,
        bool remoteWorkersAvailable,
        out QueueSettings settings,
        out SettingsRequestError? error,
        bool currentWorkerVerificationRequired = true,
        QueueSettings? currentSettings = null)
    {
        settings = default!;
        if (request.RemoteWorkersEnabled && !remoteWorkersAvailable)
        {
            return Fail(
                "workers.unavailable",
                "Remote workers are groundwork in this release, not a feature. "
                + $"Set {RemoteWorkersFeature.EnvironmentVariable}=true to try the preview.",
                out error);
        }

        if (request.MaxConcurrentJobs < 1)
            return Fail("settings.maxConcurrentJobs.minimum", "Max concurrent jobs must be at least 1.", out error);
        var workloadMode = currentSettings?.WorkloadConcurrencyMode ?? WorkloadConcurrencyMode.Automatic;
        if (request.WorkloadConcurrencyMode is { } requestedMode
            && (!Enum.TryParse(requestedMode, ignoreCase: true, out workloadMode)
                || !Enum.IsDefined(workloadMode)))
            return Fail("settings.workloadConcurrencyMode.invalid", "Workload concurrency mode must be Automatic or Manual.", out error);
        var nonVideoSlots = request.NonVideoSlots ?? currentSettings?.NonVideoSlots ?? 0;
        var evidenceSlots = request.EvidenceValidationSlots ?? currentSettings?.EvidenceValidationSlots ?? 2;
        if (nonVideoSlots is < 0 or > 4)
            return Fail("settings.nonVideoSlots.range", "Non-video slots must be between 0 and 4.", out error);
        if (evidenceSlots is < 1 or > 4)
            return Fail("settings.evidenceValidationSlots.range", "Evidence validation slots must be between 1 and 4.", out error);
        if (request.MinFreeDiskBytes < 0)
            return Fail("settings.minFreeDiskBytes.nonNegative", "Minimum free disk space cannot be negative.", out error);
        if (request.CpuThreadLimit < 0)
            return Fail("settings.cpuThreadLimit.nonNegative", "CPU thread limit cannot be negative.", out error);
        if (request.LibraryScanIntervalHours < 1)
            return Fail("settings.libraryScanIntervalHours.minimum", "Library scan interval must be at least 1 hour.", out error);
        if (request.ReplacementQuarantineRetentionDays < 0)
            // Keep the established API code alongside the persisted/wire setting name.
            return Fail("settings.quarantineRetention.nonNegative", "Cleanup retention days cannot be negative.", out error);
        if (!Enum.TryParse<EncoderMode>(request.EncoderMode, ignoreCase: true, out var encoderMode))
            return Fail("settings.encoderMode.invalid", "Encoder mode must be one of Auto, Cpu, NvidiaNvenc, IntelQsv, or Vaapi.", out error);
        var hdrToneMapMode = HdrToneMapMode.Software;
        if (!string.IsNullOrWhiteSpace(request.HdrToneMapMode)
            && (!Enum.TryParse(request.HdrToneMapMode, ignoreCase: true, out hdrToneMapMode)
                || !Enum.IsDefined(hdrToneMapMode)))
        {
            return Fail("settings.hdrToneMapMode.invalid", "HDR tone-map mode must be Software or Hardware.", out error);
        }

        settings = new QueueSettings(
            request.MaxConcurrentJobs,
            request.MinFreeDiskBytes,
            request.CpuThreadLimit,
            request.LibraryScanIntervalHours,
            encoderMode,
            request.HardwareDecode,
            hdrToneMapMode,
            VerificationPolicy.Default,
            request.ReplacementAllowCrossFilesystem,
            request.DryRunMode,
            request.ReplacementQuarantineRetentionDays,
            request.RemoteWorkersEnabled,
            request.WorkerVerificationRequired ?? currentWorkerVerificationRequired,
            workloadMode,
            nonVideoSlots,
            evidenceSlots);
        error = null;
        return true;
    }

    private static bool Fail(string code, string message, out SettingsRequestError? error)
    {
        error = new SettingsRequestError(code, message);
        return false;
    }
}
