using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Library;
using Optimisarr.Api.Realtime;
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

app.MapGet("/api/settings", async (SettingsStore settings, CancellationToken cancellationToken) =>
{
    var libraryRoot = await settings.GetLibraryRootAsync(cancellationToken);
    return Results.Ok(new { libraryRoot });
})
.WithName("GetSettings");

app.MapPut("/api/settings/library-root", async (
    SetLibraryRootRequest request,
    SettingsStore settings,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Path))
    {
        return Results.BadRequest(new { error = "A library root path is required." });
    }

    if (!Directory.Exists(request.Path))
    {
        return Results.BadRequest(new { error = $"Directory does not exist: {request.Path}" });
    }

    await settings.SetLibraryRootAsync(request.Path, cancellationToken);
    return Results.Ok(new { libraryRoot = request.Path });
})
.WithName("SetLibraryRoot");

app.MapPost("/api/library/scan", async (
    SettingsStore settings,
    LibraryInventoryService inventory,
    CancellationToken cancellationToken) =>
{
    var libraryRoot = await settings.GetLibraryRootAsync(cancellationToken);
    if (string.IsNullOrWhiteSpace(libraryRoot))
    {
        return Results.BadRequest(new { error = "No library root is configured." });
    }

    if (!Directory.Exists(libraryRoot))
    {
        return Results.BadRequest(new { error = $"Configured library root does not exist: {libraryRoot}" });
    }

    var summary = await inventory.ScanAsync(libraryRoot, cancellationToken);
    return Results.Ok(summary);
})
.WithName("ScanLibrary");

app.MapGet("/api/media", async (OptimisarrDbContext db, CancellationToken cancellationToken) =>
{
    var files = await db.MediaFiles
        .AsNoTracking()
        .OrderBy(file => file.RelativePath)
        .Select(file => new MediaFileDto(
            file.Id,
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

internal sealed record SetLibraryRootRequest(string Path);

internal sealed record MediaFileDto(
    int Id,
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
