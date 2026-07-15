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

internal static class LibraryEndpoints
{
    public static void MapLibraryEndpoints(this WebApplication app)
    {
        app.MapGet("/api/libraries", async (OptimisarrDbContext db, CancellationToken cancellationToken) =>
        {
            var libraries = await db.Libraries
                .AsNoTracking()
                .OrderBy(library => library.Name)
                .Select(library => new { Library = library, FileCount = library.MediaFiles.Count })
                .ToListAsync(cancellationToken);

            return Results.Ok(libraries.Select(row => LibraryDto.From(row.Library, row.FileCount)));
        })
        .WithName("ListLibraries");

        // Pre-flight access check for a library's path: confirms Optimisarr can reach, read, and (most
        // importantly for safe replacement) write the folder, so a permissions problem surfaces here
        // rather than as a failed replace later.
        app.MapGet("/api/libraries/{id:int}/access", async (
            int id,
            OptimisarrDbContext db,
            CancellationToken cancellationToken) =>
        {
            var library = await db.Libraries.AsNoTracking().FirstOrDefaultAsync(l => l.Id == id, cancellationToken);
            if (library is null)
            {
                return ApiErrors.NotFound("library.notFound", $"No library with id {id}.", new { id });
            }

            var (exists, readable, writable) = PathAccessProbe.Probe(library.Path);
            return Results.Ok(new
            {
                path = library.Path,
                exists,
                readable,
                writable,
                ok = LibraryAccessEvaluator.IsOk(exists, readable, writable),
                message = LibraryAccessEvaluator.Describe(exists, readable, writable),
            });
        })
        .WithName("CheckLibraryAccess");

        app.MapPost("/api/libraries", async (
            SaveLibraryRequest request,
            OptimisarrDbContext db,
            CancellationToken cancellationToken) =>
        {
            if (!LibraryRequestParser.TryParse(request, out var parsed, out var error))
            {
                return ApiErrors.BadRequest("library.validation", error!);
            }

            if (await db.Libraries.AnyAsync(library => library.Path == parsed.Path, cancellationToken))
            {
                return ApiErrors.Conflict("library.pathConflict", $"A library already exists for path: {parsed.Path}", new { path = parsed.Path });
            }

            var library = new Optimisarr.Data.Library
            {
                Name = parsed.Name,
                Path = parsed.Path,
                MediaType = parsed.MediaType,
                RuleProfile = parsed.RuleProfile,
                Enabled = parsed.Enabled,
                Priority = parsed.Priority,
                MinFileSizeBytes = parsed.MinFileSizeBytes,
                MaxHeight = parsed.MaxHeight,
                ReencodeSameCodecAboveBytes = parsed.ReencodeSameCodecAboveBytes,
                SkipEfficientSources = parsed.SkipEfficientSources,
                TargetVideoCodec = parsed.TargetVideoCodec,
                TargetContainer = parsed.TargetContainer,
                HdrHandling = parsed.HdrHandling,
                OptimiseDolbyVision = parsed.OptimiseDolbyVision,
                ExcludePaths = parsed.ExcludePaths,
                QualityCrf = parsed.QualityCrf,
                EncoderPreset = parsed.EncoderPreset,
                AudioTargetCodec = parsed.AudioTargetCodec,
                AudioBitrateKbps = parsed.AudioBitrateKbps,
                VideoAudioCodec = parsed.VideoAudioCodec,
                VideoAudioBitrateKbps = parsed.VideoAudioBitrateKbps,
                DownmixToStereo = parsed.DownmixToStereo,
                KeepAudioLanguages = parsed.KeepAudioLanguages,
                ReencodeLossyAudio = parsed.ReencodeLossyAudio,
                TargetImageFormat = parsed.TargetImageFormat,
                ImageQuality = parsed.ImageQuality,
                ReencodeLossyImages = parsed.ReencodeLossyImages,
                ImageDownscaleMode = parsed.ImageDownscaleMode,
                ImageDownscaleValue = parsed.ImageDownscaleValue,
                MoveOnComplete = parsed.MoveOnComplete,
                TargetFolder = parsed.TargetFolder,
                MoveOverwrite = parsed.MoveOverwrite,
                MinVmafHarmonicMean = parsed.MinVmafHarmonicMean,
                MinVmafMin = parsed.MinVmafMin,
                VmafQualityGateEnabled = parsed.VmafQualityGateEnabled,
                MinVmafCatastrophicMin = parsed.MinVmafCatastrophicMin,
                ClipVmafEnabled = parsed.ClipVmafEnabled,
                VmafFrameSubsample = parsed.VmafFrameSubsample,
                AutoEnqueueEnabled = parsed.AutoEnqueueEnabled,
                AutoEnqueueWindowStart = parsed.AutoEnqueueWindowStart,
                AutoEnqueueWindowEnd = parsed.AutoEnqueueWindowEnd,
                AutoReplace = parsed.AutoReplace
            };
            db.Libraries.Add(library);
            await db.SaveChangesAsync(cancellationToken);

            return Results.Created($"/api/libraries/{library.Id}", LibraryDto.From(library, 0));
        })
        .WithName("CreateLibrary");

        app.MapPut("/api/libraries/{id:int}", async (
            int id,
            SaveLibraryRequest request,
            OptimisarrDbContext db,
            CancellationToken cancellationToken) =>
        {
            var library = await db.Libraries.FirstOrDefaultAsync(l => l.Id == id, cancellationToken);
            if (library is null)
            {
                return ApiErrors.NotFound("library.notFound", $"No library with id {id}.", new { id });
            }

            if (!LibraryRequestParser.TryParse(request, out var parsed, out var error))
            {
                return ApiErrors.BadRequest("library.validation", error!);
            }

            if (await db.Libraries.AnyAsync(l => l.Path == parsed.Path && l.Id != id, cancellationToken))
            {
                return ApiErrors.Conflict("library.pathConflict", $"A library already exists for path: {parsed.Path}", new { path = parsed.Path });
            }

            library.Name = parsed.Name;
            library.Path = parsed.Path;
            library.MediaType = parsed.MediaType;
            library.RuleProfile = parsed.RuleProfile;
            library.Enabled = parsed.Enabled;
            library.Priority = parsed.Priority;
            library.MinFileSizeBytes = parsed.MinFileSizeBytes;
            library.MaxHeight = parsed.MaxHeight;
            library.ReencodeSameCodecAboveBytes = parsed.ReencodeSameCodecAboveBytes;
            library.SkipEfficientSources = parsed.SkipEfficientSources;
            library.TargetVideoCodec = parsed.TargetVideoCodec;
            library.TargetContainer = parsed.TargetContainer;
            library.HdrHandling = parsed.HdrHandling;
            library.OptimiseDolbyVision = parsed.OptimiseDolbyVision;
            library.ExcludePaths = parsed.ExcludePaths;
            library.QualityCrf = parsed.QualityCrf;
            library.EncoderPreset = parsed.EncoderPreset;
            library.AudioTargetCodec = parsed.AudioTargetCodec;
            library.AudioBitrateKbps = parsed.AudioBitrateKbps;
            library.VideoAudioCodec = parsed.VideoAudioCodec;
            library.VideoAudioBitrateKbps = parsed.VideoAudioBitrateKbps;
            library.DownmixToStereo = parsed.DownmixToStereo;
            library.KeepAudioLanguages = parsed.KeepAudioLanguages;
            library.ReencodeLossyAudio = parsed.ReencodeLossyAudio;
            library.TargetImageFormat = parsed.TargetImageFormat;
            library.ImageQuality = parsed.ImageQuality;
            library.ReencodeLossyImages = parsed.ReencodeLossyImages;
            library.ImageDownscaleMode = parsed.ImageDownscaleMode;
            library.ImageDownscaleValue = parsed.ImageDownscaleValue;
            library.MoveOnComplete = parsed.MoveOnComplete;
            library.TargetFolder = parsed.TargetFolder;
            library.MoveOverwrite = parsed.MoveOverwrite;
            library.MinVmafHarmonicMean = parsed.MinVmafHarmonicMean;
            library.MinVmafMin = parsed.MinVmafMin;
            library.VmafQualityGateEnabled = parsed.VmafQualityGateEnabled;
            library.MinVmafCatastrophicMin = parsed.MinVmafCatastrophicMin;
            library.ClipVmafEnabled = parsed.ClipVmafEnabled;
            library.VmafFrameSubsample = parsed.VmafFrameSubsample;
            library.AutoEnqueueEnabled = parsed.AutoEnqueueEnabled;
            library.AutoEnqueueWindowStart = parsed.AutoEnqueueWindowStart;
            library.AutoEnqueueWindowEnd = parsed.AutoEnqueueWindowEnd;
            library.AutoReplace = parsed.AutoReplace;
            library.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);

            var fileCount = await db.MediaFiles.CountAsync(f => f.LibraryId == id, cancellationToken);
            return Results.Ok(LibraryDto.From(library, fileCount));
        })
        .WithName("UpdateLibrary");

        app.MapDelete("/api/libraries/{id:int}", async (
            int id,
            OptimisarrDbContext db,
            CancellationToken cancellationToken) =>
        {
            var library = await db.Libraries.FirstOrDefaultAsync(l => l.Id == id, cancellationToken);
            if (library is null)
            {
                return ApiErrors.NotFound("library.notFound", $"No library with id {id}.", new { id });
            }

            db.Libraries.Remove(library);
            await db.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        })
        .WithName("DeleteLibrary");

        app.MapPost("/api/libraries/{id:int}/scan", async (
            int id,
            OptimisarrDbContext db,
            LibraryInventoryService inventory,
            CancellationToken cancellationToken) =>
        {
            var library = await db.Libraries.FirstOrDefaultAsync(l => l.Id == id, cancellationToken);
            if (library is null)
            {
                return ApiErrors.NotFound("library.notFound", $"No library with id {id}.", new { id });
            }

            if (!Directory.Exists(library.Path))
            {
                return ApiErrors.BadRequest("library.pathMissing", $"Library path does not exist: {library.Path}", new { path = library.Path });
            }

            var summary = await inventory.ScanAsync(library, cancellationToken);
            return Results.Ok(summary);
        })
        .WithName("ScanLibrary");

        app.MapPost("/api/libraries/scan", async (
            LibraryInventoryService inventory,
            CancellationToken cancellationToken) =>
        {
            var summary = await inventory.ScanEnabledAsync(cancellationToken);
            return Results.Ok(summary);
        })
        .WithName("ScanAllLibraries");
    }
}
