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
        app.MapGet("/api/system/realcugan", (SettingsStore settings, CancellationToken cancellationToken) =>
            SuperResolutionStatusAsync(RealCuganEngine.Instance.Name, null, null, settings, cancellationToken))
        .WithName("GetRealCuganStatus");

        app.MapGet("/api/system/super-resolution/{engine}", (
            string engine,
            SettingsStore settings,
            CancellationToken cancellationToken,
            string? model = null,
            int? scale = null) => SuperResolutionStatusAsync(engine, model, scale, settings, cancellationToken))
        .WithName("GetSuperResolutionStatus");

        // A real small-sample engine run. This is the only check that proves the Vulkan
        // runtime and the configured model files actually work on this machine; it can take a
        // few seconds, so it runs on the operator's explicit "test environment" action.
        app.MapPost("/api/system/realcugan/probe", (SettingsStore settings, CancellationToken cancellationToken) =>
            SuperResolutionProbeAsync(RealCuganEngine.Instance.Name, null, null, settings, cancellationToken))
        .WithName("ProbeRealCugan");

        app.MapPost("/api/system/super-resolution/{engine}/probe", (
            string engine,
            SettingsStore settings,
            CancellationToken cancellationToken,
            string? model = null,
            int? scale = null) => SuperResolutionProbeAsync(engine, model, scale, settings, cancellationToken))
        .WithName("ProbeSuperResolution");

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

    /// <summary>
    /// The configuration a status check or sample run uses: the server's settings for Real-CUGAN, and
    /// otherwise the model asked about, or the engine's first when none was named.
    /// </summary>
    private static async Task<SuperResolutionOptions?> OptionsForCheckAsync(
        string engine, string? model, int? scale, SettingsStore settings, CancellationToken cancellationToken)
    {
        var found = SuperResolutionEngines.Find(engine);
        return found is null ? null : await settings.GetSuperResolutionOptionsForAsync(
            found == RealCuganEngine.Instance ? null : engine,
            model ?? found.Presets[0],
            scale ?? found.Scales[0],
            cancellationToken);
    }

    private static IResult UnknownEngine(string engine) =>
        ApiErrors.NotFound("superResolution.engine", $"Unknown super-resolution engine '{engine}'.", new { engine });

    private static async Task<IResult> SuperResolutionStatusAsync(
        string engine, string? model, int? scale, SettingsStore settings, CancellationToken cancellationToken)
    {
        if (await OptionsForCheckAsync(engine, model, scale, settings, cancellationToken) is not { } options)
            return UnknownEngine(engine);
        var detection = await new SuperResolutionDetectionService(options).DetectAsync(cancellationToken);
        // Asked about the engine rather than one model, it must have every model a library can choose.
        if (model is null && options.Engine != RealCuganEngine.Instance)
        {
            foreach (var preset in options.Engine.Presets.Skip(1))
            {
                var usable = options.Engine.Scales.First(candidate => options.Engine.ModelFile(preset, candidate, 0) is not null);
                var other = await new SuperResolutionDetectionService(options with { Preset = preset, Scale = usable }).DetectAsync(cancellationToken);
                detection = detection with
                {
                    Readiness = detection.Readiness == SuperResolutionReadiness.Ready ? other.Readiness : detection.Readiness,
                    ModelsFound = detection.ModelsFound && other.ModelsFound,
                    MissingModelFile = detection.MissingModelFile ?? other.MissingModelFile,
                    Problems = [.. detection.Problems.Union(other.Problems)],
                };
            }
        }
        return Results.Ok(SuperResolutionStatusDto.From(detection, options));
    }

    private static async Task<IResult> SuperResolutionProbeAsync(
        string engine, string? model, int? scale, SettingsStore settings, CancellationToken cancellationToken)
    {
        if (await OptionsForCheckAsync(engine, model, scale, settings, cancellationToken) is not { } options)
            return UnknownEngine(engine);
        // A real engine run is part of the experimental feature: while it is off, nothing
        // about super-resolution executes on this machine.
        if (!options.Enabled)
        {
            return ApiErrors.Conflict("superResolution.disabled",
                "Turn on AI super-resolution in Settings before testing the engine.");
        }
        var ffmpeg = MediaToolCommands.ResolveFfmpeg(Environment.GetEnvironmentVariable("OPTIMISARR_FFMPEG"));
        var probe = await new SuperResolutionDetectionService(options).ProbeAsync(ffmpeg, cancellationToken);
        return Results.Ok(SuperResolutionProbeDto.From(probe));
    }
}

/// <summary>
/// The super-resolution engine's status as the static check found it, plus the effective
/// configuration it was checked against — so the UI can show the operator exactly what a run
/// would use and, when something is missing, exactly what to fix.
/// </summary>
internal sealed record SuperResolutionStatusDto(
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
    public static SuperResolutionStatusDto From(SuperResolutionDetection detection, SuperResolutionOptions options) => new(
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

internal sealed record SuperResolutionProbeDto(
    bool Success,
    int? ExitCode,
    string? Error,
    string? OutputTail,
    string? DeviceUsed)
{
    public static SuperResolutionProbeDto From(SuperResolutionProbeResult probe) => new(
        probe.Success,
        probe.ExitCode,
        probe.Error,
        probe.OutputTail,
        probe.DeviceUsed);
}
