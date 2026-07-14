using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Diagnostics;
using Optimisarr.Api.Library;
using Optimisarr.Api.Metrics;
using Optimisarr.Api.Queue;
using Optimisarr.Api.Realtime;
using Optimisarr.Api.Replacement;
using Optimisarr.Api.Security;
using Optimisarr.Api.Stats;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Library;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Rules;
using Optimisarr.Core.Settings;
using Optimisarr.Core.Tools;
using Optimisarr.Core.Verification;
using Optimisarr.Data;

namespace Optimisarr.Api.Endpoints;

internal static class SettingsEndpoints
{
    public static void MapSettingsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/settings", async (
            SettingsStore settings,
            CancellationToken cancellationToken) =>
        {
            var queue = await settings.GetQueueSettingsAsync(cancellationToken);
            return Results.Ok(SettingsDto.From(queue));
        })
        .WithName("GetSettings");

        app.MapPut("/api/settings", async (
            SettingsDto request,
            SettingsStore settings,
            CancellationToken cancellationToken) =>
        {
            if (request.MaxConcurrentJobs < 1)
            {
                return ApiErrors.BadRequest("settings.maxConcurrentJobs.minimum", "Max concurrent jobs must be at least 1.");
            }

            if (request.MinFreeDiskBytes < 0)
            {
                return ApiErrors.BadRequest("settings.minFreeDiskBytes.nonNegative", "Minimum free disk space cannot be negative.");
            }

            if (request.CpuThreadLimit < 0)
            {
                return ApiErrors.BadRequest("settings.cpuThreadLimit.nonNegative", "CPU thread limit cannot be negative.");
            }

            if (request.LibraryScanIntervalHours < 1)
            {
                return ApiErrors.BadRequest("settings.libraryScanIntervalHours.minimum", "Library scan interval must be at least 1 hour.");
            }

            if (request.VerificationDurationTolerancePercent < 0)
            {
                return ApiErrors.BadRequest("settings.verificationDurationTolerance.nonNegative", "Verification duration tolerance cannot be negative.");
            }

            if (request.VerificationMinimumVmafHarmonicMean is < 0 or > 100
                || request.VerificationMinimumVmafMin is < 0 or > 100)
            {
                return ApiErrors.BadRequest("settings.vmaf.range", "VMAF thresholds must be between 0 and 100.");
            }

            if (request.VerificationVmafFrameSubsample is < 1 or > QualityScoreCommandBuilder.MaximumFrameSubsample)
            {
                return ApiErrors.BadRequest(
                    "settings.vmafFrameSubsample.range",
                    $"VMAF frame subsampling must be between 1 and {QualityScoreCommandBuilder.MaximumFrameSubsample}.");
            }

            if (request.VerificationMaxLoudnessDriftLufs < 0)
            {
                return ApiErrors.BadRequest("settings.loudnessDrift.nonNegative", "Loudness drift tolerance cannot be negative.");
            }

            if (!double.IsFinite(request.VerificationMaxTruePeakDbtp))
            {
                return ApiErrors.BadRequest("settings.truePeak.finite", "True-peak ceiling must be a finite dBTP value.");
            }

            if (request.VerificationMinimumImageSsim is < 0 or > 1)
            {
                return ApiErrors.BadRequest("settings.imageSsim.range", "Image SSIM threshold must be between 0 and 1.");
            }

            if (request.ReplacementQuarantineRetentionDays < 0)
            {
                return ApiErrors.BadRequest("settings.quarantineRetention.nonNegative", "Quarantine retention days cannot be negative.");
            }

            if (!Enum.TryParse<EncoderMode>(request.EncoderMode, ignoreCase: true, out var encoderMode))
            {
                return ApiErrors.BadRequest("settings.encoderMode.invalid", "Encoder mode must be one of Auto, Cpu, NvidiaNvenc, IntelQsv, or Vaapi.");
            }

            await settings.SetQueueSettingsAsync(new QueueSettings(
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
                    request.VerificationQualityGateEnabled,
                    request.VerificationMinimumVmafHarmonicMean,
                    request.VerificationMinimumVmafMin,
                    request.VerificationAudioLoudnessGateEnabled,
                    request.VerificationMaxLoudnessDriftLufs,
                    request.VerificationAudioClippingGateEnabled,
                    request.VerificationMaxTruePeakDbtp,
                    request.VerificationImageQualityGateEnabled,
                    request.VerificationMinimumImageSsim,
                    request.VerificationImageMetadataGateEnabled,
                    request.VerificationClipVmafEnabled,
                    request.VerificationVmafFrameSubsample),
                request.ReplacementAllowCrossFilesystem,
                request.DryRunMode,
                request.ReplacementQuarantineRetentionDays), cancellationToken);

            var queue = await settings.GetQueueSettingsAsync(cancellationToken);
            return Results.Ok(SettingsDto.From(queue));
        })
        .WithName("UpdateSettings");

        // Settings backup contains configuration and provider credentials but never media,
        // job, replacement, quarantine, or rollback data. Treat the downloaded JSON as secret material.
        app.MapGet("/api/settings/export", async (
            ConfigPortabilityService portability,
            CancellationToken cancellationToken) =>
        {
            var snapshot = await portability.ExportAsync(cancellationToken);
            return Results.Ok(snapshot);
        })
        .WithName("ExportSettings");

        app.MapPost("/api/settings/import", async (
            ConfigSnapshot snapshot,
            ConfigPortabilityService portability,
            CancellationToken cancellationToken) =>
        {
            var result = await portability.ImportAsync(snapshot, cancellationToken);
            return result.Applied
                ? Results.Ok(result)
                : ApiErrors.BadRequest("settings.import.invalid", "The config file is invalid.", details: result.Errors);
        })
        .WithName("ImportSettings");

        app.MapGet("/api/queue/status", async (
            QueueDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            var status = await dispatcher.GetDispatchStatusAsync(cancellationToken);
            return Results.Ok(QueueStatusDto.From(status));
        })
        .WithName("GetQueueDispatchStatus");
    }
}
