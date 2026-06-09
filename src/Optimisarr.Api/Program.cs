using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Library;
using Optimisarr.Api.Queue;
using Optimisarr.Api.Realtime;
using Optimisarr.Api.Replacement;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Library;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Settings;
using Optimisarr.Core.Tools;
using Optimisarr.Core.Verification;
using Optimisarr.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddSignalR();
builder.Services.AddSingleton<ToolDetectionService>();
builder.Services.AddSingleton<HardwareCapabilityService>();
builder.Services.AddSingleton<LibraryScanner>();
builder.Services.AddSingleton<MediaProbeService>();
builder.Services.AddSingleton<DecodeHealthCheck>();
builder.Services.AddSingleton<QualityScoreService>();
builder.Services.AddSingleton<VerificationService>();
builder.Services.AddScoped<SettingsStore>();
builder.Services.AddScoped<ConfigPortabilityService>();
builder.Services.AddScoped<LibraryInventoryService>();
builder.Services.AddScoped<CandidateService>();
builder.Services.AddScoped<ArrActivityService>();
builder.Services.AddScoped<JobEnqueueService>();
builder.Services.AddScoped<LibraryRefreshService>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<ProviderConnectService>();
builder.Services.AddScoped<ReplacementService>();
builder.Services.AddScoped<QuarantinePurgeService>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ActivityMonitor>();
builder.Services.AddSingleton<QueueDispatcher>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<QueueDispatcher>());
builder.Services.AddHostedService<QuarantinePurgeWorker>();

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
        return Results.BadRequest(new { error = "Max concurrent jobs must be at least 1." });
    }

    if (request.MinFreeDiskBytes < 0)
    {
        return Results.BadRequest(new { error = "Minimum free disk space cannot be negative." });
    }

    if (request.CpuThreadLimit < 0)
    {
        return Results.BadRequest(new { error = "CPU thread limit cannot be negative." });
    }

    if (request.VerificationDurationTolerancePercent < 0)
    {
        return Results.BadRequest(new { error = "Verification duration tolerance cannot be negative." });
    }

    if (request.VerificationMinimumVmafHarmonicMean is < 0 or > 100
        || request.VerificationMinimumVmafMin is < 0 or > 100)
    {
        return Results.BadRequest(new { error = "VMAF thresholds must be between 0 and 100." });
    }

    if (request.ReplacementQuarantineRetentionDays < 0)
    {
        return Results.BadRequest(new { error = "Quarantine retention days cannot be negative." });
    }

    if (!Enum.TryParse<EncoderMode>(request.EncoderMode, ignoreCase: true, out var encoderMode))
    {
        return Results.BadRequest(new { error = "Encoder mode must be one of Auto, Cpu, NvidiaNvenc, IntelQsv, or Vaapi." });
    }

    if (!TryParseTime(request.ScheduleWindowStart, out var start))
    {
        return Results.BadRequest(new { error = "Schedule window start must use HH:mm format." });
    }

    if (!TryParseTime(request.ScheduleWindowEnd, out var end))
    {
        return Results.BadRequest(new { error = "Schedule window end must use HH:mm format." });
    }

    await settings.SetQueueSettingsAsync(new QueueSettings(
        request.MaxConcurrentJobs,
        request.ScheduleEnabled,
        start,
        end,
        request.MinFreeDiskBytes,
        request.CpuThreadLimit,
        encoderMode,
        new VerificationPolicy(
            request.VerificationDurationTolerancePercent,
            request.VerificationRequireAudioRetained,
            request.VerificationRequireSubtitlesRetained,
            request.VerificationRequireSizeReduction,
            request.VerificationQualityGateEnabled,
            request.VerificationMinimumVmafHarmonicMean,
            request.VerificationMinimumVmafMin),
        request.ReplacementAllowCrossFilesystem,
        request.ReplacementQuarantineRetentionDays), cancellationToken);

    var queue = await settings.GetQueueSettingsAsync(cancellationToken);
    return Results.Ok(SettingsDto.From(queue));
})
.WithName("UpdateSettings");

// Settings backup: a secret-free snapshot of settings, libraries, watchers, and
// notification targets. Tokens are never exported — they are re-entered after import.
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
        : Results.BadRequest(new { error = "The config file is invalid.", details = result.Errors });
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

// Activity watchers: media servers Optimisarr polls so the queue pauses while
// someone is streaming. Tokens are write-only — they are never returned.
app.MapGet("/api/activity-watchers", async (OptimisarrDbContext db, CancellationToken cancellationToken) =>
{
    var watchers = await db.ActivityWatchers
        .AsNoTracking()
        .OrderBy(watcher => watcher.Name)
        .ToListAsync(cancellationToken);
    return Results.Ok(watchers.Select(ActivityWatcherDto.From));
})
.WithName("ListActivityWatchers");

app.MapPost("/api/activity-watchers", async (
    SaveActivityWatcherRequest request,
    OptimisarrDbContext db,
    CancellationToken cancellationToken) =>
{
    if (!ActivityWatcherRequestParser.TryParse(request, out var parsed, out var error))
    {
        return Results.BadRequest(new { error });
    }

    var watcher = new ActivityWatcher
    {
        Name = parsed.Name,
        Type = parsed.Type,
        BaseUrl = parsed.BaseUrl,
        ApiToken = parsed.ApiToken,
        Enabled = parsed.Enabled,
        RefreshOnReplace = parsed.RefreshOnReplace
    };
    db.ActivityWatchers.Add(watcher);
    await db.SaveChangesAsync(cancellationToken);

    return Results.Created($"/api/activity-watchers/{watcher.Id}", ActivityWatcherDto.From(watcher));
})
.WithName("CreateActivityWatcher");

app.MapPut("/api/activity-watchers/{id:int}", async (
    int id,
    SaveActivityWatcherRequest request,
    OptimisarrDbContext db,
    CancellationToken cancellationToken) =>
{
    var watcher = await db.ActivityWatchers.FirstOrDefaultAsync(w => w.Id == id, cancellationToken);
    if (watcher is null)
    {
        return Results.NotFound(new { error = $"No activity watcher with id {id}." });
    }

    if (!ActivityWatcherRequestParser.TryParse(request, out var parsed, out var error))
    {
        return Results.BadRequest(new { error });
    }

    watcher.Name = parsed.Name;
    watcher.Type = parsed.Type;
    watcher.BaseUrl = parsed.BaseUrl;
    // A blank token on update keeps the stored secret rather than wiping it.
    if (parsed.ApiToken is not null)
    {
        watcher.ApiToken = parsed.ApiToken;
    }
    watcher.Enabled = parsed.Enabled;
    watcher.RefreshOnReplace = parsed.RefreshOnReplace;
    watcher.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(cancellationToken);

    return Results.Ok(ActivityWatcherDto.From(watcher));
})
.WithName("UpdateActivityWatcher");

app.MapDelete("/api/activity-watchers/{id:int}", async (
    int id,
    OptimisarrDbContext db,
    CancellationToken cancellationToken) =>
{
    var watcher = await db.ActivityWatchers.FirstOrDefaultAsync(w => w.Id == id, cancellationToken);
    if (watcher is null)
    {
        return Results.NotFound(new { error = $"No activity watcher with id {id}." });
    }

    db.ActivityWatchers.Remove(watcher);
    await db.SaveChangesAsync(cancellationToken);
    return Results.NoContent();
})
.WithName("DeleteActivityWatcher");

// Notification targets: where Optimisarr POSTs on noteworthy events. Tokens are
// write-only — they are never returned.
app.MapGet("/api/notification-targets", async (OptimisarrDbContext db, CancellationToken cancellationToken) =>
{
    var targets = await db.NotificationTargets
        .AsNoTracking()
        .OrderBy(target => target.Name)
        .ToListAsync(cancellationToken);
    return Results.Ok(targets.Select(NotificationTargetDto.From));
})
.WithName("ListNotificationTargets");

app.MapPost("/api/notification-targets", async (
    SaveNotificationTargetRequest request,
    OptimisarrDbContext db,
    CancellationToken cancellationToken) =>
{
    if (!NotificationTargetRequestParser.TryParse(request, out var parsed, out var error))
    {
        return Results.BadRequest(new { error });
    }

    var target = new NotificationTarget
    {
        Name = parsed.Name,
        Type = parsed.Type,
        Url = parsed.Url,
        Token = parsed.Token,
        Enabled = parsed.Enabled,
        NotifyOnReplacement = parsed.NotifyOnReplacement,
        NotifyOnFailure = parsed.NotifyOnFailure
    };
    db.NotificationTargets.Add(target);
    await db.SaveChangesAsync(cancellationToken);

    return Results.Created($"/api/notification-targets/{target.Id}", NotificationTargetDto.From(target));
})
.WithName("CreateNotificationTarget");

app.MapPut("/api/notification-targets/{id:int}", async (
    int id,
    SaveNotificationTargetRequest request,
    OptimisarrDbContext db,
    CancellationToken cancellationToken) =>
{
    var target = await db.NotificationTargets.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
    if (target is null)
    {
        return Results.NotFound(new { error = $"No notification target with id {id}." });
    }

    if (!NotificationTargetRequestParser.TryParse(request, out var parsed, out var error))
    {
        return Results.BadRequest(new { error });
    }

    target.Name = parsed.Name;
    target.Type = parsed.Type;
    target.Url = parsed.Url;
    // A blank token on update keeps the stored secret rather than wiping it.
    if (parsed.Token is not null)
    {
        target.Token = parsed.Token;
    }
    target.Enabled = parsed.Enabled;
    target.NotifyOnReplacement = parsed.NotifyOnReplacement;
    target.NotifyOnFailure = parsed.NotifyOnFailure;
    target.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(cancellationToken);

    return Results.Ok(NotificationTargetDto.From(target));
})
.WithName("UpdateNotificationTarget");

app.MapDelete("/api/notification-targets/{id:int}", async (
    int id,
    OptimisarrDbContext db,
    CancellationToken cancellationToken) =>
{
    var target = await db.NotificationTargets.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
    if (target is null)
    {
        return Results.NotFound(new { error = $"No notification target with id {id}." });
    }

    db.NotificationTargets.Remove(target);
    await db.SaveChangesAsync(cancellationToken);
    return Results.NoContent();
})
.WithName("DeleteNotificationTarget");

// Sonarr/Radarr connections: managers Optimisarr asks about in-progress imports so a
// file isn't optimised while an import is landing in its folder. Keys are write-only.
app.MapGet("/api/arr-connections", async (OptimisarrDbContext db, CancellationToken cancellationToken) =>
{
    var connections = await db.ArrConnections
        .AsNoTracking()
        .OrderBy(connection => connection.Name)
        .ToListAsync(cancellationToken);
    return Results.Ok(connections.Select(ArrConnectionDto.From));
})
.WithName("ListArrConnections");

app.MapPost("/api/arr-connections", async (
    SaveArrConnectionRequest request,
    OptimisarrDbContext db,
    CancellationToken cancellationToken) =>
{
    if (!ArrConnectionRequestParser.TryParse(request, out var parsed, out var error))
    {
        return Results.BadRequest(new { error });
    }

    var connection = new ArrConnection
    {
        Name = parsed.Name,
        Type = parsed.Type,
        BaseUrl = parsed.BaseUrl,
        ApiKey = parsed.ApiKey,
        Enabled = parsed.Enabled
    };
    db.ArrConnections.Add(connection);
    await db.SaveChangesAsync(cancellationToken);

    return Results.Created($"/api/arr-connections/{connection.Id}", ArrConnectionDto.From(connection));
})
.WithName("CreateArrConnection");

app.MapPut("/api/arr-connections/{id:int}", async (
    int id,
    SaveArrConnectionRequest request,
    OptimisarrDbContext db,
    CancellationToken cancellationToken) =>
{
    var connection = await db.ArrConnections.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
    if (connection is null)
    {
        return Results.NotFound(new { error = $"No arr connection with id {id}." });
    }

    if (!ArrConnectionRequestParser.TryParse(request, out var parsed, out var error))
    {
        return Results.BadRequest(new { error });
    }

    connection.Name = parsed.Name;
    connection.Type = parsed.Type;
    connection.BaseUrl = parsed.BaseUrl;
    // A blank key on update keeps the stored secret rather than wiping it.
    if (parsed.ApiKey is not null)
    {
        connection.ApiKey = parsed.ApiKey;
    }
    connection.Enabled = parsed.Enabled;
    connection.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(cancellationToken);

    return Results.Ok(ArrConnectionDto.From(connection));
})
.WithName("UpdateArrConnection");

app.MapDelete("/api/arr-connections/{id:int}", async (
    int id,
    OptimisarrDbContext db,
    CancellationToken cancellationToken) =>
{
    var connection = await db.ArrConnections.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
    if (connection is null)
    {
        return Results.NotFound(new { error = $"No arr connection with id {id}." });
    }

    db.ArrConnections.Remove(connection);
    await db.SaveChangesAsync(cancellationToken);
    return Results.NoContent();
})
.WithName("DeleteArrConnection");

// Interactive sign-in: acquire a provider token without the user pasting a raw one.
// Each flow is start-then-poll; the client opens the auth URL / shows the code, then
// polls until the user approves. A failure to reach the provider is a 502.
app.MapPost("/api/connect/plex/start", async (
    ProviderConnectService connect,
    CancellationToken cancellationToken) =>
{
    try
    {
        var start = await connect.StartPlexAsync(cancellationToken);
        return Results.Ok(start);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        return Results.Problem($"Could not start Plex sign-in: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
    }
})
.WithName("StartPlexConnect");

app.MapGet("/api/connect/plex/poll", async (
    long id,
    ProviderConnectService connect,
    CancellationToken cancellationToken) =>
{
    try
    {
        var result = await connect.PollPlexAsync(id, cancellationToken);
        return Results.Ok(result);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        return Results.Problem($"Could not check Plex sign-in: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
    }
})
.WithName("PollPlexConnect");

app.MapPost("/api/connect/jellyfin/start", async (
    JellyfinConnectRequest request,
    ProviderConnectService connect,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.BaseUrl))
    {
        return Results.BadRequest(new { error = "Enter the Jellyfin server's base URL first." });
    }

    try
    {
        var start = await connect.StartJellyfinAsync(request.BaseUrl, cancellationToken);
        return Results.Ok(start);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        return Results.Problem($"Could not start Quick Connect: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
    }
})
.WithName("StartJellyfinConnect");

app.MapPost("/api/connect/jellyfin/poll", async (
    JellyfinPollRequest request,
    ProviderConnectService connect,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.BaseUrl) || string.IsNullOrWhiteSpace(request.Secret))
    {
        return Results.BadRequest(new { error = "Quick Connect session details are missing." });
    }

    try
    {
        var result = await connect.PollJellyfinAsync(request.BaseUrl, request.Secret, cancellationToken);
        return Results.Ok(result);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        return Results.Problem($"Could not check Quick Connect: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
    }
})
.WithName("PollJellyfinConnect");

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
    CancellationToken cancellationToken) =>
{
    var result = await hardware.DetectAsync(cancellationToken);
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
        EncoderPreset = parsed.EncoderPreset,
        MoveOnComplete = parsed.MoveOnComplete,
        TargetFolder = parsed.TargetFolder
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
    library.MoveOnComplete = parsed.MoveOnComplete;
    library.TargetFolder = parsed.TargetFolder;
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

app.MapGet("/api/jobs", async (OptimisarrDbContext db, CancellationToken cancellationToken) =>
    Results.Ok(await JobQueries.ListAsync(db, cancellationToken)))
.WithName("ListJobs");

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

app.MapGet("/api/replacements", async (OptimisarrDbContext db, CancellationToken cancellationToken) =>
    Results.Ok(await ReplacementQueries.ListAsync(db, cancellationToken)))
.WithName("ListReplacements");

app.MapPost("/api/replacements/{id:int}/rollback", async (
    int id,
    ReplacementService replacement,
    CancellationToken cancellationToken) =>
{
    var result = await replacement.RollbackAsync(id, cancellationToken);
    return ReplacementResults.ToHttp(result);
})
.WithName("RollbackReplacement");

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

static bool TryParseTime(string? value, out TimeOnly time)
{
    time = default;
    return value is not null &&
        TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
}

internal sealed record SettingsDto(
    int MaxConcurrentJobs,
    bool ScheduleEnabled,
    string? ScheduleWindowStart,
    string? ScheduleWindowEnd,
    long MinFreeDiskBytes,
    int CpuThreadLimit,
    string EncoderMode,
    double VerificationDurationTolerancePercent,
    bool VerificationRequireAudioRetained,
    bool VerificationRequireSubtitlesRetained,
    bool VerificationRequireSizeReduction,
    bool VerificationQualityGateEnabled,
    double VerificationMinimumVmafHarmonicMean,
    double VerificationMinimumVmafMin,
    bool ReplacementAllowCrossFilesystem,
    int ReplacementQuarantineRetentionDays)
{
    public static SettingsDto From(QueueSettings settings) => new(
        settings.MaxConcurrentJobs,
        settings.ScheduleEnabled,
        FormatTime(settings.ScheduleWindowStart),
        FormatTime(settings.ScheduleWindowEnd),
        settings.MinFreeDiskBytes,
        settings.CpuThreadLimit,
        settings.EncoderMode.ToString(),
        settings.VerificationPolicy.DurationTolerancePercent,
        settings.VerificationPolicy.RequireAudioRetained,
        settings.VerificationPolicy.RequireSubtitlesRetained,
        settings.VerificationPolicy.RequireSizeReduction,
        settings.VerificationPolicy.QualityGateEnabled,
        settings.VerificationPolicy.MinimumVmafHarmonicMean,
        settings.VerificationPolicy.MinimumVmafMin,
        settings.ReplacementAllowCrossFilesystem,
        settings.ReplacementQuarantineRetentionDays);

    private static string FormatTime(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);
}

internal sealed record QueueStatusDto(
    bool CanStart,
    string? BlockedReason,
    int RunningJobs,
    int MaxConcurrentJobs,
    bool ScheduleEnabled,
    string ScheduleWindowStart,
    string ScheduleWindowEnd,
    long MinFreeDiskBytes,
    int CpuThreadLimit,
    string EncoderMode,
    long? FreeDiskBytes,
    string WorkRoot)
{
    public static QueueStatusDto From(QueueDispatchStatus status) => new(
        status.CanStart,
        status.BlockedReason,
        status.RunningJobs,
        status.MaxConcurrentJobs,
        status.ScheduleEnabled,
        FormatTime(status.ScheduleWindowStart),
        FormatTime(status.ScheduleWindowEnd),
        status.MinFreeDiskBytes,
        status.CpuThreadLimit,
        status.EncoderMode.ToString(),
        status.FreeDiskBytes,
        status.WorkRoot);

    private static string FormatTime(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);
}

internal sealed record JellyfinConnectRequest(string? BaseUrl);

internal sealed record JellyfinPollRequest(string? BaseUrl, string? Secret);

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
    string? EncoderPreset,
    bool? MoveOnComplete,
    string? TargetFolder);

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
    bool MoveOnComplete,
    string? TargetFolder,
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
        library.MoveOnComplete,
        library.TargetFolder,
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
