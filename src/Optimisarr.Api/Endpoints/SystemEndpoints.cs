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
            // The concrete codec/container/CRF each profile resolves to, straight from RuleProfileDefaults,
            // so the preset slider can show exactly what every position selects without the UI hard-coding
            // (and drifting from) the backend's choices.
            ruleProfileSpecs = Enum.GetValues<RuleProfile>().Select(profile =>
            {
                var rules = RuleProfileDefaults.For(profile);
                return new
                {
                    profile = profile.ToString(),
                    codec = rules.TargetVideoCodec,
                    container = rules.TargetContainer,
                    crf = rules.DefaultCrf
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
