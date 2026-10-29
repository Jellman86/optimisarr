using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Diagnostics;
using Optimisarr.Api.Endpoints;
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
using Optimisarr.Core.SuperResolution;
using Optimisarr.Core.Tools;
using Optimisarr.Core.Verification;
using Optimisarr.Data;

namespace Optimisarr.Api.Endpoints;

internal static class SystemEndpoints
{
    public static void MapSystemEndpoints(this WebApplication app)
    {
        app.MapGet("/api/system/tools", async (
            ToolDetectionService tools,
            CancellationToken cancellationToken) =>
        {
            var results = await tools.DetectAsync(cancellationToken);
            return Results.Ok(new
            {
                checkedAt = DateTimeOffset.UtcNow,
                tools = results
            });
        })
        .WithName("GetSystemTools");

        // Static super-resolution engine check: binary, model files, Vulkan loader. Cheap and
        // side-effect free; the UI calls it when the super-resolution settings are shown.
        app.MapGet("/api/system/realcugan", async (
            SettingsStore settings,
            CancellationToken cancellationToken) =>
        {
            var options = await settings.GetSuperResolutionOptionsAsync(cancellationToken);
            var detection = await new RealCuganDetectionService(options).DetectAsync(cancellationToken);
            return Results.Ok(RealCuganStatusDto.From(detection, options));
        })
        .WithName("GetRealCuganStatus");

        // A real small-sample engine run. This is the only check that proves the Vulkan
        // runtime and the configured model files actually work on this machine; it can take a
        // few seconds, so it runs on the operator's explicit "test environment" action.
        app.MapPost("/api/system/realcugan/probe", async (
            SettingsStore settings,
            CancellationToken cancellationToken) =>
        {
            var options = await settings.GetSuperResolutionOptionsAsync(cancellationToken);
            var ffmpeg = MediaToolCommands.ResolveFfmpeg(
                Environment.GetEnvironmentVariable("OPTIMISARR_FFMPEG"));
            var probe = await new RealCuganDetectionService(options).ProbeAsync(ffmpeg, cancellationToken);
            return Results.Ok(RealCuganProbeDto.From(probe));
        })
        .WithName("ProbeRealCugan");

        app.MapGet("/api/system/hardware", async (
            HardwareCapabilityService hardware,
            bool? refresh,
            CancellationToken cancellationToken) =>
        {
            var result = await hardware.DetectAsync(cancellationToken, forceRefresh: refresh ?? false);
            return Results.Ok(new
            {
                checkedAt = DateTimeOffset.UtcNow,
                hardware = result
            });
        })
        .WithName("GetHardwareCapabilities");

        // Lists immediate subdirectories of a path so the UI can offer a folder picker
        // instead of free-text paths. Defaults to /data (the conventional media mount).
        app.MapGet("/api/fs/browse", (string? path) =>
        {
            var target = string.IsNullOrWhiteSpace(path)
                ? (Directory.Exists("/data") ? "/data" : "/")
                : path;

            if (!Directory.Exists(target))
            {
                return ApiErrors.BadRequest("filesystem.notDirectory", $"Not a directory: {target}", new { path = target });
            }

            var fullPath = Path.GetFullPath(target);
            var parent = Directory.GetParent(fullPath)?.FullName;

            var directories = new List<DirectoryEntry>();
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(fullPath).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    var name = Path.GetFileName(dir);
                    if (!string.IsNullOrEmpty(name))
                    {
                        directories.Add(new DirectoryEntry(name, dir));
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                return ApiErrors.BadRequest("filesystem.accessDenied", $"Access denied: {fullPath}", new { path = fullPath });
            }

            return Results.Ok(new BrowseResponse(fullPath, parent, directories));
        })
        .WithName("BrowseFileSystem");

        // The valid enum values for library media types and rule profiles, so the UI
        // can render selectors without hard-coding the backend's vocabulary.
        app.MapGet("/api/library-options", () => Results.Ok(new
        {
            mediaTypes = Enum.GetNames<MediaType>(),
            ruleProfiles = Enum.GetNames<RuleProfile>(),
            // The complete encoding bundle each profile resolves to, straight from RuleProfileDefaults,
            // so the preset slider can apply and show exactly what every position selects without the UI
            // hard-coding (and drifting from) the backend's choices.
            ruleProfileSpecs = Enum.GetValues<RuleProfile>().Select(profile =>
            {
                var rules = RuleProfileDefaults.For(profile);
                return new
                {
                    profile = profile.ToString(),
                    codec = rules.TargetVideoCodec,
                    container = rules.TargetContainer,
                    crf = rules.DefaultCrf,
                    hdrHandling = rules.Hdr.ToString(),
                    videoAudioCodec = rules.VideoAudioCodec,
                    videoAudioBitrateKbps = rules.VideoAudioBitrateKbps,
                    audioTargetCodec = rules.TargetAudioCodec,
                    audioBitrateKbps = rules.AudioBitrateKbps,
                    downmixToStereo = rules.DownmixToStereo
                };
            }),
            hdrHandlings = Enum.GetNames<HdrHandling>(),
            videoCodecs = new[] { "hevc", "h264", "av1" },
            containers = new[] { "mkv", "mp4" },
            // Image targets proven against the exact FFmpeg build used for production jobs.
            imageFormats = ImageTarget.EncodableFormats,
            // Portable effort choices. The dispatcher resolves these after selecting the exact
            // encoder, so Auto mode never forwards an x264 value to NVENC/QSV/SVT-AV1.
            encoderPresets = EncoderPresetPolicy.Selections,
            // Recognised values from earlier releases and direct API use. The UI keeps an existing
            // one visible until the operator deliberately replaces it with a portable choice.
            legacyEncoderPresets = EncoderPresetPolicy.LegacySelections
        }))
        .WithName("GetLibraryOptions");
    }
}

/// <summary>
/// The super-resolution engine's status as the static check found it, plus the effective
/// configuration it was checked against — so the UI can show the operator exactly what a run
/// would use and, when something is missing, exactly what to fix.
/// </summary>
internal sealed record RealCuganStatusDto(
    string Engine,
    string Readiness,
    bool BinaryFound,
    string? BinaryPath,
    bool ModelsFound,
    string? ModelsRoot,
    string? ModelDirectory,
    string? MissingModelFile,
    bool VulkanLoaderAvailable,
    bool Enabled,
    string Preset,
    int Scale,
    int NoiseLevel,
    string Device,
    IReadOnlyList<string> Problems)
{
    public static RealCuganStatusDto From(RealCuganDetection detection, SuperResolutionOptions options) => new(
        detection.Engine,
        detection.Readiness.ToString(),
        detection.BinaryFound,
        detection.BinaryPath,
        detection.ModelsFound,
        detection.ModelsRoot,
        detection.ModelDirectory,
        detection.MissingModelFile,
        detection.VulkanLoaderAvailable,
        options.Enabled,
        options.Preset,
        options.Scale,
        options.NoiseLevel,
        options.Device,
        detection.Problems);
}

internal sealed record RealCuganProbeDto(
    bool Success,
    int? ExitCode,
    string? Error,
    string? OutputTail,
    string? DeviceUsed)
{
    public static RealCuganProbeDto From(RealCuganProbeResult probe) => new(
        probe.Success,
        probe.ExitCode,
        probe.Error,
        probe.OutputTail,
        probe.DeviceUsed);
}
