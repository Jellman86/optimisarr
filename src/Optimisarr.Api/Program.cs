using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Library;
using Optimisarr.Api.Realtime;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Library;
using Optimisarr.Core.Tools;
using Optimisarr.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddSignalR();
builder.Services.AddSingleton<ToolDetectionService>();
builder.Services.AddSingleton<LibraryScanner>();
builder.Services.AddSingleton<MediaProbeService>();
builder.Services.AddScoped<SettingsStore>();
builder.Services.AddScoped<LibraryInventoryService>();
builder.Services.AddScoped<CandidateService>();

var configDirectory = ResolveConfigDirectory(builder.Environment);
Directory.CreateDirectory(configDirectory);

var databasePath = Path.Combine(configDirectory, "optimisarr.db");
builder.Services.AddDbContext<OptimisarrDbContext>(options =>
{
    options.UseSqlite($"Data Source={databasePath}");
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    // Migrations are versioned and idempotent: applying an up-to-date database
    // is a no-op, so this is safe to run on every startup.
    var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
    await db.Database.MigrateAsync();

    var settings = scope.ServiceProvider.GetRequiredService<SettingsStore>();
    await LibrarySeeder.MigrateLegacyLibraryRootAsync(db, settings, CancellationToken.None);
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/health", () => Results.Ok(new
{
    status = "healthy",
    service = "optimisarr",
    version = typeof(Program).Assembly.GetName().Version?.ToString(),
    checkedAt = DateTimeOffset.UtcNow
}))
.WithName("GetHealth");

app.MapGet("/api/settings", async (
    SettingsStore settings,
    CancellationToken cancellationToken) =>
{
    var maxConcurrentJobs = await settings.GetMaxConcurrentJobsAsync(cancellationToken);
    return Results.Ok(new SettingsDto(maxConcurrentJobs));
})
.WithName("GetSettings");

app.MapPut("/api/settings", async (
    SettingsDto request,
    SettingsStore settings,
    CancellationToken cancellationToken) =>
{
    if (request.MaxConcurrentJobs < 1)
    {
        return Results.BadRequest(new { error = "Max concurrent jobs must be at least 1." });
    }

    await settings.SetMaxConcurrentJobsAsync(request.MaxConcurrentJobs, cancellationToken);
    var maxConcurrentJobs = await settings.GetMaxConcurrentJobsAsync(cancellationToken);
    return Results.Ok(new SettingsDto(maxConcurrentJobs));
})
.WithName("UpdateSettings");

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
    hdrHandlings = Enum.GetNames<HdrHandling>(),
    videoCodecs = new[] { "hevc", "h264", "av1" },
    containers = new[] { "mkv", "mp4" },
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
        TargetVideoCodec = parsed.TargetVideoCodec,
        TargetContainer = parsed.TargetContainer,
        HdrHandling = parsed.HdrHandling,
        ExcludePaths = parsed.ExcludePaths,
        QualityCrf = parsed.QualityCrf,
        EncoderPreset = parsed.EncoderPreset
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
    library.TargetVideoCodec = parsed.TargetVideoCodec;
    library.TargetContainer = parsed.TargetContainer;
    library.HdrHandling = parsed.HdrHandling;
    library.ExcludePaths = parsed.ExcludePaths;
    library.QualityCrf = parsed.QualityCrf;
    library.EncoderPreset = parsed.EncoderPreset;
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

app.MapGet("/api/media", async (
    int? libraryId,
    OptimisarrDbContext db,
    CancellationToken cancellationToken) =>
{
    var query = db.MediaFiles.AsNoTracking();
    if (libraryId is not null)
    {
        query = query.Where(file => file.LibraryId == libraryId);
    }

    var files = await query
        .OrderBy(file => file.RelativePath)
        .Select(file => new MediaFileDto(
            file.Id,
            file.LibraryId,
            file.RelativePath,
            file.SizeBytes,
            file.Status.ToString(),
            file.Container,
            file.VideoCodec,
            file.Width,
            file.Height,
            file.DurationSeconds,
            file.AudioCodecs,
            file.AudioTrackCount,
            file.SubtitleTrackCount,
            file.ProbedAt,
            file.ProbeError))
        .ToListAsync(cancellationToken);

    return Results.Ok(files);
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

app.MapHub<JobsHub>("/hubs/jobs");

app.MapFallbackToFile("index.html");

await app.RunAsync();

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

internal sealed record SettingsDto(int MaxConcurrentJobs);

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
    string? TargetVideoCodec,
    string? TargetContainer,
    string? HdrHandling,
    string? ExcludePaths,
    int? QualityCrf,
    string? EncoderPreset);

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
    string? TargetVideoCodec,
    string? TargetContainer,
    string? HdrHandling,
    string? ExcludePaths,
    int? QualityCrf,
    string? EncoderPreset,
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
        library.TargetVideoCodec,
        library.TargetContainer,
        library.HdrHandling?.ToString(),
        library.ExcludePaths,
        library.QualityCrf,
        library.EncoderPreset,
        fileCount,
        library.CreatedAt,
        library.UpdatedAt);
}

internal sealed record MediaFileDto(
    int Id,
    int? LibraryId,
    string RelativePath,
    long SizeBytes,
    string Status,
    string? Container,
    string? VideoCodec,
    int? Width,
    int? Height,
    double? DurationSeconds,
    string? AudioCodecs,
    int? AudioTrackCount,
    int? SubtitleTrackCount,
    DateTimeOffset? ProbedAt,
    string? ProbeError);

// Exposed so the test host (WebApplicationFactory) and EF tooling can locate the entry assembly.
public partial class Program;
