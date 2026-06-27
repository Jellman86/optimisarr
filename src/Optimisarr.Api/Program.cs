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

var builder = WebApplication.CreateBuilder(args);
var adminToken = Environment.GetEnvironmentVariable(AdminTokenAuth.EnvironmentVariable)?.Trim();

builder.Services.AddOpenApi();
builder.Services.AddSignalR();
builder.Services.AddSingleton<ToolDetectionService>();
// The transcoding/detection ffmpeg. Defaults to "ffmpeg" on PATH, but can be pointed at a
// hardware-capable build (e.g. jellyfin-ffmpeg, which bundles Intel iHD + oneVPL and NVENC)
// via OPTIMISARR_FFMPEG. Detection and transcode share it so the encoder list never lies
// about what the encoder actually is.
var transcodeFfmpeg = Environment.GetEnvironmentVariable("OPTIMISARR_FFMPEG");
builder.Services.AddSingleton(new TranscodeOptions(
    string.IsNullOrWhiteSpace(transcodeFfmpeg) ? "ffmpeg" : transcodeFfmpeg));
builder.Services.AddSingleton(new HardwareCapabilityService(transcodeFfmpeg));
builder.Services.AddSingleton<LibraryScanner>();
builder.Services.AddSingleton<MediaProbeService>();
builder.Services.AddSingleton<DecodeHealthCheck>();
builder.Services.AddSingleton<TimestampIntegrityCheck>();
// VMAF/loudness measurement needs an ffmpeg built with libvmaf, which may be a
// different binary from the transcoding ffmpeg (e.g. jellyfin-ffmpeg). Point it via
// OPTIMISARR_FFMPEG_VMAF; falls back to "ffmpeg" on PATH.
var vmafFfmpeg = Environment.GetEnvironmentVariable("OPTIMISARR_FFMPEG_VMAF");
builder.Services.AddSingleton(new QualityScoreService(vmafFfmpeg));
builder.Services.AddSingleton(new LoudnessService(vmafFfmpeg));
builder.Services.AddSingleton(new ImageQualityService(vmafFfmpeg));
// The portable image marker is written/read with exiftool (ffmpeg's still encoders drop
// -metadata). Point at a specific binary via OPTIMISARR_EXIFTOOL; falls back to "exiftool" on PATH.
builder.Services.AddSingleton(new ImageMarkerService(Environment.GetEnvironmentVariable("OPTIMISARR_EXIFTOOL")));
builder.Services.AddSingleton(new ImageMetadataService(Environment.GetEnvironmentVariable("OPTIMISARR_EXIFTOOL")));
builder.Services.AddSingleton<VerificationService>();
builder.Services.AddScoped<SettingsStore>();
builder.Services.AddScoped<ConfigPortabilityService>();
builder.Services.AddScoped<LibraryInventoryService>();
builder.Services.AddScoped<CandidateService>();
builder.Services.AddScoped<ArrActivityService>();
builder.Services.AddScoped<JobEnqueueService>();
builder.Services.AddScoped<PreviewService>();
builder.Services.AddScoped<LibraryRefreshService>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<ProviderConnectService>();
builder.Services.AddSingleton<ReplacementCoordinator>();
builder.Services.AddScoped<ReplacementService>();
builder.Services.AddScoped<LifetimeStatsStore>();
builder.Services.AddScoped<QuarantinePurgeService>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ActivityMonitor>();
builder.Services.AddSingleton<ActiveEncodeRegistry>();
// Singleton so its resolved-artwork cache survives across requests.
builder.Services.AddSingleton<ArtworkService>();
builder.Services.AddSingleton<QueueDispatcher>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<QueueDispatcher>());
builder.Services.AddHostedService<SystemMetricsBroadcaster>();
builder.Services.AddHostedService<QuarantinePurgeWorker>();
builder.Services.AddHostedService<AutoEnqueueWorker>();
builder.Services.AddHostedService<LibraryScanWorker>();
builder.Services.AddHostedService<MediaProbeWorker>();

var configDirectory = ResolveConfigDirectory(builder.Environment);
Directory.CreateDirectory(configDirectory);

var databasePath = Path.Combine(configDirectory, "optimisarr.db");
builder.Services.AddDbContext<OptimisarrDbContext>(options =>
{
    options.UseSqlite($"Data Source={databasePath}");
});

var app = builder.Build();

if (!AdminTokenAuth.Required(adminToken))
{
    app.Logger.LogWarning(
        "{EnvironmentVariable} is not set. Optimisarr has no built-in authentication; keep it on a trusted network or behind an authenticated reverse proxy.",
        AdminTokenAuth.EnvironmentVariable);
}

using (var scope = app.Services.CreateScope())
{
    // Migrations are versioned and idempotent: applying an up-to-date database
    // is a no-op, so this is safe to run on every startup.
    var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
    await db.Database.MigrateAsync();

    var settings = scope.ServiceProvider.GetRequiredService<SettingsStore>();
    await LibrarySeeder.MigrateLegacyLibraryRootAsync(db, settings, CancellationToken.None);

    // One-time: re-probe files left as Unknown by databases predating media-kind classification.
    await MediaKindBackfill.ResetUnknownProbedFilesAsync(db, CancellationToken.None);
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Content-hashed assets (Vite output under /assets) can cache forever; index.html must never be
// cached, or a browser keeps loading the previous deploy's asset hashes and never sees new UI.
var staticFileOptions = new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        var headers = ctx.Context.Response.Headers;
        if (ctx.File.Name.Equals("index.html", StringComparison.OrdinalIgnoreCase))
        {
            headers.CacheControl = "no-cache, no-store, must-revalidate";
        }
        else if (ctx.Context.Request.Path.StartsWithSegments("/assets"))
        {
            headers.CacheControl = "public, max-age=31536000, immutable";
        }
    },
};

app.UseDefaultFiles();
app.UseStaticFiles(staticFileOptions);

app.Use(async (context, next) =>
{
    if (!AdminTokenAuth.Required(adminToken)
        || AdminTokenAuth.IsOpenPath(context.Request.Path)
        || !AdminTokenAuth.IsProtectedPath(context.Request.Path))
    {
        await next();
        return;
    }

    if (AdminTokenAuth.IsAuthorized(context.Request, adminToken!))
    {
        await next();
        return;
    }

    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
    await context.Response.WriteAsJsonAsync(new { error = "Admin token required." }, context.RequestAborted);
});

app.MapGet("/api/health", () => Results.Ok(new
{
    status = "healthy",
    service = "optimisarr",
    version = typeof(Program).Assembly.GetName().Version?.ToString(),
    checkedAt = DateTimeOffset.UtcNow
}))
.WithName("GetHealth");

app.MapGet("/api/ready", async (
    OptimisarrDbContext db,
    ToolDetectionService tools,
    CancellationToken cancellationToken) =>
{
    var failures = new List<string>();
    if (!await db.Database.CanConnectAsync(cancellationToken))
    {
        failures.Add("database is unavailable");
    }

    foreach (var path in new[] { configDirectory, "/work", "/trash" })
    {
        if (!Directory.Exists(path) || !PathAccessProbe.CanWrite(path))
        {
            failures.Add($"required path is not writable: {path}");
        }
    }

    var unavailableTools = (await tools.DetectAsync(cancellationToken))
        .Where(tool => !tool.Available)
        .Select(tool => tool.Name)
        .ToList();
    if (unavailableTools.Count > 0)
    {
        failures.Add($"required tools unavailable: {string.Join(", ", unavailableTools)}");
    }

    return failures.Count == 0
        ? Results.Ok(new { status = "ready" })
        : Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, detail: string.Join("; ", failures));
})
.WithName("GetReadiness");

app.MapGet("/api/auth/status", () => Results.Ok(new
{
    required = AdminTokenAuth.Required(adminToken)
}))
.WithName("GetAuthStatus");

app.MapSettingsEndpoints();

app.MapIntegrationEndpoints();

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
        return Results.BadRequest(new { error = $"Not a directory: {target}" });
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
        return Results.BadRequest(new { error = $"Access denied: {fullPath}" });
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
    // Image targets whose encode is wired today (WebP); AVIF/JXL follow once their encode lands.
    imageFormats = ImageTarget.EncodableFormats,
    // x264/x265 speed presets; slower = smaller files for the same quality.
    encoderPresets = new[]
    {
        "ultrafast", "superfast", "veryfast", "faster", "fast",
        "medium", "slow", "slower", "veryslow"
    }
}))
.WithName("GetLibraryOptions");

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
        return Results.NotFound(new { error = $"No library with id {id}." });
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
        return Results.BadRequest(new { error });
    }

    if (await db.Libraries.AnyAsync(library => library.Path == parsed.Path, cancellationToken))
    {
        return Results.Conflict(new { error = $"A library already exists for path: {parsed.Path}" });
    }

    var library = new Library
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
        TargetVideoCodec = parsed.TargetVideoCodec,
        TargetContainer = parsed.TargetContainer,
        HdrHandling = parsed.HdrHandling,
        ExcludePaths = parsed.ExcludePaths,
        QualityCrf = parsed.QualityCrf,
        EncoderPreset = parsed.EncoderPreset,
        AudioTargetCodec = parsed.AudioTargetCodec,
        AudioBitrateKbps = parsed.AudioBitrateKbps,
        VideoAudioCodec = parsed.VideoAudioCodec,
        VideoAudioBitrateKbps = parsed.VideoAudioBitrateKbps,
        DownmixToStereo = parsed.DownmixToStereo,
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
        return Results.NotFound(new { error = $"No library with id {id}." });
    }

    if (!LibraryRequestParser.TryParse(request, out var parsed, out var error))
    {
        return Results.BadRequest(new { error });
    }

    if (await db.Libraries.AnyAsync(l => l.Path == parsed.Path && l.Id != id, cancellationToken))
    {
        return Results.Conflict(new { error = $"A library already exists for path: {parsed.Path}" });
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
    library.TargetVideoCodec = parsed.TargetVideoCodec;
    library.TargetContainer = parsed.TargetContainer;
    library.HdrHandling = parsed.HdrHandling;
    library.ExcludePaths = parsed.ExcludePaths;
    library.QualityCrf = parsed.QualityCrf;
    library.EncoderPreset = parsed.EncoderPreset;
    library.AudioTargetCodec = parsed.AudioTargetCodec;
    library.AudioBitrateKbps = parsed.AudioBitrateKbps;
    library.VideoAudioCodec = parsed.VideoAudioCodec;
    library.VideoAudioBitrateKbps = parsed.VideoAudioBitrateKbps;
    library.DownmixToStereo = parsed.DownmixToStereo;
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
        return Results.NotFound(new { error = $"No library with id {id}." });
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
        return Results.NotFound(new { error = $"No library with id {id}." });
    }

    if (!Directory.Exists(library.Path))
    {
        return Results.BadRequest(new { error = $"Library path does not exist: {library.Path}" });
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

// The inventory list. Optional `libraryId`, `status` (a MediaFileStatus), and `search` (a
// case-insensitive substring of the relative path) narrow the set; `page`/`pageSize` page it. The
// body stays a MediaFileDto array (existing callers are unaffected); the pre-paging total is returned
// in the X-Total-Count header. Filtering, counting, ordering, and paging all run in the database.
app.MapGet("/api/media", async (
    int? libraryId,
    string? status,
    string? search,
    int? page,
    int? pageSize,
    HttpResponse response,
    OptimisarrDbContext db,
    CancellationToken cancellationToken) =>
{
    MediaFileStatus? wantedStatus = null;
    if (!string.IsNullOrWhiteSpace(status))
    {
        if (!Enum.TryParse<MediaFileStatus>(status, ignoreCase: true, out var parsed))
        {
            return Results.BadRequest(
                $"Unknown media status '{status}'. Valid values: {string.Join(", ", Enum.GetNames<MediaFileStatus>())}.");
        }
        wantedStatus = parsed;
    }

    var result = await MediaQueries.QueryAsync(db, new MediaQuery
    {
        LibraryId = libraryId,
        Status = wantedStatus,
        Search = search,
        Page = page ?? 1,
        PageSize = pageSize ?? 0
    }, cancellationToken);

    response.Headers["X-Total-Count"] = result.Total.ToString();
    return Results.Ok(result.Items);
})
.WithName("ListMedia");

app.MapPost("/api/media/{id:int}/probe", async (
    int id,
    LibraryInventoryService inventory,
    CancellationToken cancellationToken) =>
{
    var file = await inventory.ProbeAsync(id, cancellationToken);
    if (file is null)
    {
        return Results.NotFound(new { error = $"No media file with id {id}." });
    }

    return Results.Ok(new MediaFileDto(
        file.Id,
        file.LibraryId,
        file.RelativePath,
        file.SizeBytes,
        file.Status.ToString(),
        file.MediaKind.ToString(),
        file.Container,
        file.VideoCodec,
        file.Width,
        file.Height,
        file.DurationSeconds,
        file.AudioCodecs,
        file.AudioTrackCount,
        file.SubtitleTrackCount,
        file.ProbedAt,
        file.ProbeError));
})
.WithName("ProbeMedia");

// Phase 2: optimisation candidates derived from the probed inventory, with a
// human-readable reason for every file (eligible or skipped). No FFmpeg runs here.
app.MapGet("/api/candidates", async (
    int? libraryId,
    CandidateService candidates,
    CancellationToken cancellationToken) =>
{
    var results = await candidates.EvaluateAsync(libraryId, cancellationToken);
    return Results.Ok(results);
})
.WithName("ListCandidates");

// Per-library eligible/skipped tallies for the Libraries workspace, so the list can show each
// library's candidate counts without fetching every probed file row.
app.MapGet("/api/candidates/summary", async (
    CandidateService candidates,
    CancellationToken cancellationToken) =>
{
    var summary = await candidates.SummariseAsync(cancellationToken);
    return Results.Ok(summary);
})
.WithName("CandidateSummary");

// Phase 3: enqueue a library's eligible candidates as transcode jobs.
app.MapPost("/api/libraries/{id:int}/enqueue", async (
    int id,
    OptimisarrDbContext db,
    JobEnqueueService enqueue,
    QueueDispatcher dispatcher,
    CancellationToken cancellationToken) =>
{
    var library = await db.Libraries.FirstOrDefaultAsync(l => l.Id == id, cancellationToken);
    if (library is null)
    {
        return Results.NotFound(new { error = $"No library with id {id}." });
    }

    var result = await enqueue.EnqueueEligibleAsync(library, cancellationToken);
    if (result.Enqueued > 0)
    {
        dispatcher.Wake();
    }
    return Results.Ok(result);
})
.WithName("EnqueueLibrary");

// Phase 11: settings preview. Queue a throwaway transcode of one file with its library's
// resolved settings so the operator can compare original vs encoded before committing.
app.MapPost("/api/media/{id:int}/preview", async (
    int id,
    PreviewService preview,
    CancellationToken cancellationToken) =>
{
    var jobId = await preview.CreateAsync(id, cancellationToken);
    return jobId is null
        ? Results.NotFound(new { error = $"No media file with id {id} (or it has no library)." })
        : Results.Ok(new { jobId });
})
.WithName("CreatePreview");

app.MapGet("/api/preview/{jobId:int}", async (
    int jobId,
    PreviewService preview,
    CancellationToken cancellationToken) =>
{
    var comparison = await preview.GetAsync(jobId, cancellationToken);
    return comparison is null ? Results.NotFound() : Results.Ok(comparison);
})
.WithName("GetPreview");

app.MapDelete("/api/preview/{jobId:int}", async (
    int jobId,
    PreviewService preview,
    CancellationToken cancellationToken) =>
{
    var deleted = await preview.DeleteAsync(jobId, cancellationToken);
    return deleted ? Results.NoContent() : Results.NotFound();
})
.WithName("DeletePreview");

// Streams the original media file for side-by-side comparison (range-enabled so the browser
// can seek video/audio). Paths come from the database, never from the request.
app.MapGet("/api/media/{id:int}/content", async (
    int id,
    OptimisarrDbContext db,
    CancellationToken cancellationToken) =>
{
    var path = await db.MediaFiles
        .Where(file => file.Id == id)
        .Select(file => file.Path)
        .FirstOrDefaultAsync(cancellationToken);
    return ServeFile(path);
})
.WithName("GetMediaContent");

// Streams a preview job's encoded output for the comparison view.
app.MapGet("/api/preview/{jobId:int}/content", async (
    int jobId,
    OptimisarrDbContext db,
    CancellationToken cancellationToken) =>
{
    var path = await db.Jobs
        .Where(job => job.Id == jobId && job.Type == JobType.Preview)
        .Select(job => job.WorkOutputPath)
        .FirstOrDefaultAsync(cancellationToken);
    return ServeFile(path);
})
.WithName("GetPreviewContent");

// The queue feed and the diagnostics list. All query params are optional: status, libraryId, and
// category (a FailureCategory) narrow the set; since/until (ISO-8601) bound a job's finished/enqueued
// time; page/pageSize page the result. The body stays a JobDto array (so existing callers are
// unaffected); the pre-paging total is returned in the X-Total-Count header.
app.MapGet("/api/jobs", async (
    string? status,
    int? libraryId,
    string? category,
    DateTimeOffset? since,
    DateTimeOffset? until,
    int? page,
    int? pageSize,
    HttpResponse response,
    OptimisarrDbContext db,
    CancellationToken cancellationToken) =>
{
    JobStatus? wantedStatus = null;
    if (!string.IsNullOrWhiteSpace(status))
    {
        if (!Enum.TryParse<JobStatus>(status, ignoreCase: true, out var parsed))
        {
            return Results.BadRequest(
                $"Unknown job status '{status}'. Valid values: {string.Join(", ", Enum.GetNames<JobStatus>())}.");
        }
        wantedStatus = parsed;
    }

    FailureCategory? wantedCategory = null;
    if (!string.IsNullOrWhiteSpace(category))
    {
        if (!Enum.TryParse<FailureCategory>(category, ignoreCase: true, out var parsed))
        {
            return Results.BadRequest(
                $"Unknown failure category '{category}'. Valid values: {string.Join(", ", Enum.GetNames<FailureCategory>())}.");
        }
        wantedCategory = parsed;
    }

    var result = await JobQueries.QueryAsync(db, new JobQuery
    {
        Status = wantedStatus,
        LibraryId = libraryId,
        Category = wantedCategory,
        Since = since,
        Until = until,
        Page = page ?? 1,
        PageSize = pageSize ?? 0
    }, cancellationToken);

    response.Headers["X-Total-Count"] = result.Total.ToString();
    return Results.Ok(result.Items);
})
.WithName("ListJobs");

// Diagnostics: failed jobs grouped by classified reason (size-saving, container incompatibility,
// replacement collision, …) with counts and recent samples, so "why are jobs failing?" is answerable
// from the API without reading container logs. Optionally scoped to one library.
app.MapGet("/api/jobs/failures", async (int? libraryId, OptimisarrDbContext db, CancellationToken cancellationToken) =>
    Results.Ok(await JobQueries.SummariseFailuresAsync(db, cancellationToken, libraryId)))
.WithName("JobFailureSummary");

// The captured ffmpeg log (non-progress stderr) for a failed job, as plain text, so the full reason
// is available from the API without container access. 404 when the job is unknown or has no log
// (only failed transcodes capture one).
app.MapGet("/api/jobs/{id:int}/log", async (int id, OptimisarrDbContext db, CancellationToken cancellationToken) =>
{
    var log = await db.Jobs
        .AsNoTracking()
        .Where(job => job.Id == id)
        .Select(job => job.ProcessLog)
        .FirstOrDefaultAsync(cancellationToken);

    return string.IsNullOrEmpty(log) ? Results.NotFound() : Results.Text(log, "text/plain");
})
.WithName("JobLog");

// Backdrop artwork for a job's title, proxied from the first connected media server. 404 when
// there's no server, no match, or the file isn't film/TV — the hero just shows no background then.
app.MapGet("/api/jobs/{id:int}/artwork", async (
    int id,
    ArtworkService artwork,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    var result = await artwork.GetBackdropAsync(id, cancellationToken);
    if (result is null)
    {
        return Results.NotFound();
    }

    context.Response.Headers.CacheControl = "public, max-age=86400";
    return Results.File(result.Value.Bytes, result.Value.ContentType);
})
.WithName("JobArtwork");

// Thumbnail for a media file, by kind: a poster (Radarr/Sonarr first, then a media server) for video,
// the embedded cover art for audio, and a down-scaled still for an image. Bytes are produced/proxied
// by the backend so no server token reaches the browser. 404 when nothing is available — the UI then
// shows its plain placeholder.
app.MapGet("/api/media/{id:int}/thumbnail", async (
    int id,
    ArtworkService artwork,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    var result = await artwork.GetThumbnailAsync(id, cancellationToken);
    if (result is null)
    {
        return Results.NotFound();
    }

    context.Response.Headers.CacheControl = "public, max-age=86400";
    return Results.File(result.Value.Bytes, result.Value.ContentType);
})
.WithName("MediaThumbnail");

// Remove finished jobs (completed, failed, cancelled) to declutter the queue. A job whose
// original is still in quarantine is the live rollback path and is kept; clearing it would
// destroy a recorded rollback, which the safety standard forbids. Re-optimisation of the
// cleared files is still prevented by the embedded optimisation marker, so losing the
// history rows is safe.
app.MapPost("/api/jobs/clear", async (string? scope, OptimisarrDbContext db, CancellationToken cancellationToken) =>
{
    // scope: "finished" = completed only; "errored" = failed/cancelled; anything else (or
    // omitted) = every terminal job. The IsClearable safety check below still protects jobs
    // that hold a live rollback regardless of scope.
    var statuses = (scope?.Trim().ToLowerInvariant()) switch
    {
        "finished" => new[] { JobStatus.Completed },
        "errored" => new[] { JobStatus.Failed, JobStatus.Cancelled },
        _ => new[] { JobStatus.Completed, JobStatus.Failed, JobStatus.Cancelled },
    };

    var liveRollbackJobIds = (await db.Replacements
            .Where(r => r.Status == ReplacementStatus.Replaced)
            .Select(r => r.JobId)
            .ToListAsync(cancellationToken))
        .ToHashSet();

    var terminal = await db.Jobs
        .Where(j => statuses.Contains(j.Status))
        .ToListAsync(cancellationToken);

    var clearable = terminal.Where(j => JobClearing.IsClearable(j, liveRollbackJobIds)).ToList();
    var clearableIds = clearable.Select(j => j.Id).ToList();

    // The Job→Replacement FK is Restrict, so spent rollback records (rolled back or purged)
    // for these jobs must be removed before their parent job.
    var spentReplacements = await db.Replacements
        .Where(r => clearableIds.Contains(r.JobId))
        .ToListAsync(cancellationToken);
    db.Replacements.RemoveRange(spentReplacements);
    db.Jobs.RemoveRange(clearable);
    await db.SaveChangesAsync(cancellationToken);

    return Results.Ok(new { cleared = clearable.Count });
})
.WithName("ClearJobs");

// Reset the pending queue (e.g. after a rules change): cancel anything in flight, then remove all
// queued and ready-to-replace jobs and discard their /work outputs. No original is touched — see
// QueueDispatcher.ClearPendingQueueAsync.
app.MapPost("/api/jobs/clear-pending", async (QueueDispatcher dispatcher, CancellationToken cancellationToken) =>
{
    var cleared = await dispatcher.ClearPendingQueueAsync(cancellationToken);
    return Results.Ok(new { cleared });
})
.WithName("ClearPendingJobs");

app.MapPost("/api/jobs/{id:int}/cancel", async (
    int id,
    OptimisarrDbContext db,
    QueueDispatcher dispatcher,
    CancellationToken cancellationToken) =>
{
    var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == id, cancellationToken);
    if (job is null)
    {
        return Results.NotFound(new { error = $"No job with id {id}." });
    }

    if (!JobEnqueueService.ActiveStatuses.Contains(job.Status))
    {
        return Results.BadRequest(new { error = $"Job {id} is already {job.Status} and cannot be cancelled." });
    }

    job.Status = JobStatus.Cancelled;
    job.FinishedAt = DateTimeOffset.UtcNow;
    job.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(cancellationToken);

    // Stop the running ffmpeg, if this job is in flight.
    dispatcher.RequestCancel(job.Id);

    return Results.Ok(new { id = job.Id, status = job.Status.ToString() });
})
.WithName("CancelJob");

// Removes a cancelled or failed job so an operator can reset it and enqueue it again.
// Completed replacements are deliberately excluded: their job record protects rollback state.
app.MapDelete("/api/jobs/{id:int}", async (
    int id,
    OptimisarrDbContext db,
    CancellationToken cancellationToken) =>
{
    var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == id, cancellationToken);
    if (job is null)
    {
        return Results.NotFound(new { error = $"No job with id {id}." });
    }

    if (job.Status is not (JobStatus.Failed or JobStatus.Cancelled))
    {
        return Results.BadRequest(new { error = "Stop an active job before removing it from the queue." });
    }

    db.Jobs.Remove(job);
    await db.SaveChangesAsync(cancellationToken);
    return Results.NoContent();
})
.WithName("RemoveResettableJob");

// Re-queue a failed or cancelled job so the user can deliberately try a file again
// (e.g. after fixing an encoder setting). The history guard otherwise holds failed
// files back, so this is the explicit way to retry one.
app.MapPost("/api/jobs/{id:int}/retry", async (
    int id,
    OptimisarrDbContext db,
    QueueDispatcher dispatcher,
    CancellationToken cancellationToken) =>
{
    var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == id, cancellationToken);
    if (job is null)
    {
        return Results.NotFound(new { error = $"No job with id {id}." });
    }

    if (job.Status is not (JobStatus.Failed or JobStatus.Cancelled))
    {
        return Results.BadRequest(new { error = $"Only failed or cancelled jobs can be retried; job {id} is {job.Status}." });
    }

    job.Status = JobStatus.Queued;
    job.ErrorMessage = null;
    job.Progress = 0;
    job.StartedAt = null;
    job.FinishedAt = null;
    job.WorkOutputPath = null;
    job.OutputSizeBytes = null;
    job.VerificationPassed = null;
    job.VerificationReportJson = null;
    job.VerifiedAt = null;
    job.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(cancellationToken);

    dispatcher.Wake();
    return Results.Ok(new { id = job.Id, status = job.Status.ToString() });
})
.WithName("RetryJob");

// --- Exclusions: files the operator never wants optimised. Durable and path-keyed, so they
// survive clearing the queue and re-scanning — unlike the soft "previously failed" skip. ---

app.MapExclusionEndpoints();

// Phase 5: safe replacement. A verified ReadyToReplace job can replace its
// original — the original is quarantined first and the move is recorded so it can
// always be rolled back.
app.MapPost("/api/jobs/{id:int}/replace", async (
    int id,
    ReplacementService replacement,
    CancellationToken cancellationToken) =>
{
    var result = await replacement.ReplaceAsync(id, cancellationToken);
    return ReplacementResults.ToHttp(result);
})
.WithName("ReplaceFromJob");

// Aggregate outcome/queue/library figures for the Dashboard.
app.MapGet("/api/stats", async (
    OptimisarrDbContext db, LifetimeStatsStore lifetimeStats, CancellationToken cancellationToken) =>
{
    var lifetime = await lifetimeStats.GetAsync(cancellationToken);
    return Results.Ok(await StatsQueries.GetAsync(db, lifetime, cancellationToken));
})
.WithName("GetStats");

// Admin-only support snapshot: version, environment, settings, library and integration summaries,
// stats, and the failure summary — assembled from non-secret data only (no provider tokens, API keys,
// or webhook URLs). It is under /api, so the admin-token middleware protects it when a token is set.
app.MapGet("/api/diagnostics", async (
    OptimisarrDbContext db,
    SettingsStore settings,
    LifetimeStatsStore lifetimeStats,
    CancellationToken cancellationToken) =>
{
    var environment = new DiagnosticsEnvironment(
        RuntimeInformation.OSDescription,
        RuntimeInformation.FrameworkDescription,
        configDirectory,
        PathAccessProbe.CanWrite(configDirectory));
    var version = typeof(Program).Assembly.GetName().Version?.ToString();

    return Results.Ok(await DiagnosticsQueries.BuildAsync(
        db, settings, lifetimeStats, environment, version, cancellationToken));
})
.WithName("GetDiagnostics");

// Reset the persistent lifetime "total space saved" tally to zero. This only clears the headline
// figure the operator chose to reset; it touches no files, quarantine, or rollback history.
app.MapPost("/api/stats/clear", async (
    OptimisarrDbContext db, LifetimeStatsStore lifetimeStats, CancellationToken cancellationToken) =>
{
    await lifetimeStats.ClearAsync(cancellationToken);
    return Results.Ok(await StatsQueries.GetAsync(db, LifetimeStats.Empty, cancellationToken));
})
.WithName("ClearStats");

app.MapGet("/api/replacements", async (OptimisarrDbContext db, CancellationToken cancellationToken) =>
    Results.Ok(await ReplacementQueries.ListAsync(db, cancellationToken)))
.WithName("ListReplacements");

// Remove spent quarantine entries (rolled back, or purged after retention/approval) to declutter.
// These are terminal history with no rollback left, so clearing them never touches a live original;
// active "Replaced" entries are always kept.
app.MapPost("/api/replacements/clear", async (OptimisarrDbContext db, CancellationToken cancellationToken) =>
{
    var spent = await db.Replacements
        .Where(r => r.Status == ReplacementStatus.RolledBack || r.Status == ReplacementStatus.Purged)
        .ToListAsync(cancellationToken);
    db.Replacements.RemoveRange(spent);
    await db.SaveChangesAsync(cancellationToken);
    return Results.Ok(new { cleared = spent.Count });
})
.WithName("ClearSpentReplacements");

// One replacement with its job's verification report, for the Quarantine compare-to-approve view.
app.MapGet("/api/replacements/{id:int}", async (
    int id,
    OptimisarrDbContext db,
    CancellationToken cancellationToken) =>
{
    var detail = await ReplacementQueries.GetAsync(db, id, cancellationToken);
    return detail is null ? Results.NotFound(new { error = $"No replacement with id {id}." }) : Results.Ok(detail);
})
.WithName("GetReplacement");

// Streams the two sides of a replacement for the Quarantine visual compare: the quarantined
// original (under /trash) and the in-place replacement (the encoded file now at the media path).
app.MapGet("/api/replacements/{id:int}/original/content", async (
    int id,
    OptimisarrDbContext db,
    CancellationToken cancellationToken) =>
{
    var path = await db.Replacements
        .Where(r => r.Id == id)
        .Select(r => r.QuarantinePath)
        .FirstOrDefaultAsync(cancellationToken);
    return ServeFile(path);
})
.WithName("GetReplacementOriginalContent");

app.MapGet("/api/replacements/{id:int}/replacement/content", async (
    int id,
    OptimisarrDbContext db,
    CancellationToken cancellationToken) =>
{
    var path = await db.Replacements
        .Where(r => r.Id == id)
        .Select(r => r.FinalPath)
        .FirstOrDefaultAsync(cancellationToken);
    return ServeFile(path);
})
.WithName("GetReplacementReplacementContent");

app.MapPost("/api/replacements/{id:int}/rollback", async (
    int id,
    ReplacementService replacement,
    CancellationToken cancellationToken) =>
{
    var result = await replacement.RollbackAsync(id, cancellationToken);
    return ReplacementResults.ToHttp(result);
})
.WithName("RollbackReplacement");

// Approve a replacement: delete the quarantined original now (reclaim space) instead of waiting
// for the retention window. The replacement is kept; this only discards the rollback path.
app.MapPost("/api/replacements/{id:int}/approve", async (
    int id,
    QuarantinePurgeService purge,
    CancellationToken cancellationToken) =>
{
    var result = await purge.PurgeOneAsync(id, cancellationToken);
    return ReplacementResults.ToHttp(result);
})
.WithName("ApproveReplacement");

app.MapHub<JobsHub>("/hubs/jobs");

app.MapFallbackToFile("index.html", staticFileOptions);

await app.RunAsync();

// Serves a media file from an absolute, database-sourced path with range support so the browser
// can seek video/audio. Returns 404 when the path is missing or the file no longer exists.
static IResult ServeFile(string? path)
{
    if (string.IsNullOrEmpty(path) || !File.Exists(path))
    {
        return Results.NotFound();
    }

    var contentType = new FileExtensionContentTypeProvider().TryGetContentType(path, out var resolved)
        ? resolved
        : "application/octet-stream";
    return Results.File(path, contentType, enableRangeProcessing: true);
}

static string ResolveConfigDirectory(IHostEnvironment environment)
{
    var configured = Environment.GetEnvironmentVariable("OPTIMISARR_CONFIG_DIR");
    if (!string.IsNullOrWhiteSpace(configured))
    {
        return configured;
    }

    return Directory.Exists("/config")
        ? "/config"
        : Path.Combine(environment.ContentRootPath, "config");
}

internal sealed record SettingsDto(
    int MaxConcurrentJobs,
    long MinFreeDiskBytes,
    int CpuThreadLimit,
    int LibraryScanIntervalHours,
    string EncoderMode,
    bool HardwareDecode,
    double VerificationDurationTolerancePercent,
    bool VerificationRequireAudioRetained,
    bool VerificationRequireSubtitlesRetained,
    bool VerificationRequireSizeReduction,
    bool VerificationQualityGateEnabled,
    double VerificationMinimumVmafHarmonicMean,
    double VerificationMinimumVmafMin,
    bool VerificationAudioLoudnessGateEnabled,
    double VerificationMaxLoudnessDriftLufs,
    bool VerificationAudioClippingGateEnabled,
    double VerificationMaxTruePeakDbtp,
    bool VerificationImageQualityGateEnabled,
    double VerificationMinimumImageSsim,
    bool VerificationImageMetadataGateEnabled,
    bool ReplacementAllowCrossFilesystem,
    bool DryRunMode,
    int ReplacementQuarantineRetentionDays)
{
    public static SettingsDto From(QueueSettings settings) => new(
        settings.MaxConcurrentJobs,
        settings.MinFreeDiskBytes,
        settings.CpuThreadLimit,
        settings.LibraryScanIntervalHours,
        settings.EncoderMode.ToString(),
        settings.HardwareDecode,
        settings.VerificationPolicy.DurationTolerancePercent,
        settings.VerificationPolicy.RequireAudioRetained,
        settings.VerificationPolicy.RequireSubtitlesRetained,
        settings.VerificationPolicy.RequireSizeReduction,
        settings.VerificationPolicy.QualityGateEnabled,
        settings.VerificationPolicy.MinimumVmafHarmonicMean,
        settings.VerificationPolicy.MinimumVmafMin,
        settings.VerificationPolicy.AudioLoudnessGateEnabled,
        settings.VerificationPolicy.MaxLoudnessDriftLufs,
        settings.VerificationPolicy.AudioClippingGateEnabled,
        settings.VerificationPolicy.MaxTruePeakDbtp,
        settings.VerificationPolicy.ImageQualityGateEnabled,
        settings.VerificationPolicy.MinimumImageSsim,
        settings.VerificationPolicy.ImageMetadataGateEnabled,
        settings.ReplacementAllowCrossFilesystem,
        settings.DryRunMode,
        settings.ReplacementQuarantineRetentionDays);
}

internal sealed record QueueStatusDto(
    bool CanStart,
    string? BlockedReason,
    int RunningJobs,
    int MaxConcurrentJobs,
    long MinFreeDiskBytes,
    int CpuThreadLimit,
    string EncoderMode,
    bool HardwareAccelerated,
    long? FreeDiskBytes,
    string WorkRoot,
    string? WaitingReason)
{
    public static QueueStatusDto From(QueueDispatchStatus status) => new(
        status.CanStart,
        status.BlockedReason,
        status.RunningJobs,
        status.MaxConcurrentJobs,
        status.MinFreeDiskBytes,
        status.CpuThreadLimit,
        status.EncoderMode.ToString(),
        status.HardwareAccelerated,
        status.FreeDiskBytes,
        status.WorkRoot,
        status.WaitingReason);
}

internal sealed record JellyfinConnectRequest(string? BaseUrl);

internal sealed record JellyfinPollRequest(string? BaseUrl, string? Secret);

internal sealed record PlexServersRequest(string? Token);

internal sealed record ConnectionTestRequest(string? Type, string? BaseUrl, string? Token, int? Id);

internal sealed record DirectoryEntry(string Name, string Path);

internal sealed record BrowseResponse(string Path, string? Parent, IReadOnlyList<DirectoryEntry> Directories);

internal sealed record SaveLibraryRequest(
    string? Name,
    string? Path,
    string? MediaType,
    string? RuleProfile,
    bool? Enabled,
    int? Priority,
    long? MinFileSizeBytes,
    int? MaxHeight,
    long? ReencodeSameCodecAboveBytes,
    string? TargetVideoCodec,
    string? TargetContainer,
    string? HdrHandling,
    string? ExcludePaths,
    int? QualityCrf,
    string? EncoderPreset,
    string? AudioTargetCodec,
    int? AudioBitrateKbps,
    string? VideoAudioCodec,
    int? VideoAudioBitrateKbps,
    bool? DownmixToStereo,
    bool? ReencodeLossyAudio,
    string? TargetImageFormat,
    int? ImageQuality,
    bool? ReencodeLossyImages,
    string? ImageDownscaleMode,
    int? ImageDownscaleValue,
    bool? MoveOnComplete,
    string? TargetFolder,
    bool? MoveOverwrite,
    double? MinVmafHarmonicMean,
    double? MinVmafMin,
    bool? AutoEnqueueEnabled,
    string? AutoEnqueueWindowStart,
    string? AutoEnqueueWindowEnd,
    bool? AutoReplace);

internal sealed record ExcludeRequest(int MediaFileId, string? Reason);

internal sealed record ExclusionDto(
    int Id,
    string Path,
    int? LibraryId,
    string? RelativePath,
    string? Reason,
    string Source,
    DateTimeOffset CreatedAt);

internal sealed record LibraryDto(
    int Id,
    string Name,
    string Path,
    string MediaType,
    string RuleProfile,
    bool Enabled,
    int Priority,
    long? MinFileSizeBytes,
    int? MaxHeight,
    long? ReencodeSameCodecAboveBytes,
    string? TargetVideoCodec,
    string? TargetContainer,
    string? HdrHandling,
    string? ExcludePaths,
    int? QualityCrf,
    string? EncoderPreset,
    string? AudioTargetCodec,
    int? AudioBitrateKbps,
    string? VideoAudioCodec,
    int? VideoAudioBitrateKbps,
    bool DownmixToStereo,
    bool ReencodeLossyAudio,
    string? TargetImageFormat,
    int? ImageQuality,
    bool ReencodeLossyImages,
    string ImageDownscaleMode,
    int ImageDownscaleValue,
    bool MoveOnComplete,
    string? TargetFolder,
    bool MoveOverwrite,
    double? MinVmafHarmonicMean,
    double? MinVmafMin,
    bool AutoEnqueueEnabled,
    string AutoEnqueueWindowStart,
    string AutoEnqueueWindowEnd,
    DateTimeOffset? LastAutoEnqueueAt,
    bool AutoReplace,
    int FileCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static LibraryDto From(Library library, int fileCount) => new(
        library.Id,
        library.Name,
        library.Path,
        library.MediaType.ToString(),
        library.RuleProfile.ToString(),
        library.Enabled,
        library.Priority,
        library.MinFileSizeBytes,
        library.MaxHeight,
        library.ReencodeSameCodecAboveBytes,
        library.TargetVideoCodec,
        library.TargetContainer,
        library.HdrHandling?.ToString(),
        library.ExcludePaths,
        library.QualityCrf,
        library.EncoderPreset,
        library.AudioTargetCodec,
        library.AudioBitrateKbps,
        library.VideoAudioCodec,
        library.VideoAudioBitrateKbps,
        library.DownmixToStereo,
        library.ReencodeLossyAudio,
        library.TargetImageFormat,
        library.ImageQuality,
        library.ReencodeLossyImages,
        library.ImageDownscaleMode.ToString(),
        library.ImageDownscaleValue,
        library.MoveOnComplete,
        library.TargetFolder,
        library.MoveOverwrite,
        library.MinVmafHarmonicMean,
        library.MinVmafMin,
        library.AutoEnqueueEnabled,
        library.AutoEnqueueWindowStart.ToString("HH:mm", CultureInfo.InvariantCulture),
        library.AutoEnqueueWindowEnd.ToString("HH:mm", CultureInfo.InvariantCulture),
        library.LastAutoEnqueueAt,
        library.AutoReplace,
        fileCount,
        library.CreatedAt,
        library.UpdatedAt);
}

// Exposed so the test host (WebApplicationFactory) and EF tooling can locate the entry assembly.
public partial class Program;
