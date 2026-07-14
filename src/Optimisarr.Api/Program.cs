using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Api;
using Optimisarr.Api.Diagnostics;
using Optimisarr.Api.Endpoints;
using Optimisarr.Api.Library;
using Optimisarr.Api.Metrics;
using Optimisarr.Api.OpenApi;
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

builder.Services.AddOpenApi(options => options.AddDocumentTransformer<OptimisarrOpenApiTransformer>());
builder.Services.AddSignalR();
// Long encodes should survive a routine container update. QueueDispatcher drains active work and
// only cancels it when this bounded shutdown window is exhausted.
builder.Services.Configure<HostOptions>(options =>
    options.ShutdownTimeout = TimeSpan.FromHours(2));
// The transcoding/detection ffmpeg. Defaults to "ffmpeg" on PATH, but can be pointed at a
// hardware-capable build (e.g. jellyfin-ffmpeg, which bundles Intel iHD + oneVPL and NVENC)
// via OPTIMISARR_FFMPEG. Detection and transcode share it so the encoder list never lies
// about what the encoder actually is.
var transcodeFfmpeg = MediaToolCommands.ResolveFfmpeg(
    Environment.GetEnvironmentVariable("OPTIMISARR_FFMPEG"));
var ffprobe = MediaToolCommands.ResolveFfprobe(
    Environment.GetEnvironmentVariable("OPTIMISARR_FFPROBE"), transcodeFfmpeg);
builder.Services.AddSingleton(new TranscodeOptions(transcodeFfmpeg));
builder.Services.AddSingleton(new HardwareCapabilityService(transcodeFfmpeg));
builder.Services.AddSingleton<LibraryScanner>();
builder.Services.AddSingleton(new MediaProbeService(ffprobe));
builder.Services.AddSingleton(new DecodeHealthCheck(transcodeFfmpeg));
builder.Services.AddSingleton(new TimestampIntegrityCheck(ffprobe));
// VMAF/loudness measurement needs an ffmpeg built with libvmaf, which may be a
// different binary from the transcoding ffmpeg (e.g. jellyfin-ffmpeg). Point it via
// OPTIMISARR_FFMPEG_VMAF; falls back to "ffmpeg" on PATH. A purpose-built CUDA variant may be
// supplied separately; QualityScoreService checks its filter and falls back to this CPU binary.
var vmafFfmpeg = Environment.GetEnvironmentVariable("OPTIMISARR_FFMPEG_VMAF");
var cudaVmafFfmpeg = Environment.GetEnvironmentVariable("OPTIMISARR_FFMPEG_VMAF_CUDA");
builder.Services.AddSingleton(new ToolDetectionService(transcodeFfmpeg, vmafFfmpeg, ffprobe, cudaVmafFfmpeg));
builder.Services.AddSingleton(new QualityScoreService(vmafFfmpeg, cudaVmafFfmpeg));
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
builder.Services.AddScoped<InventoryQueries>();
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

    // Replacement and rollback intent is recorded before the first filesystem move. Reconcile any
    // interrupted operation before queue workers start so no job can race its own recovery.
    var replacement = scope.ServiceProvider.GetRequiredService<ReplacementService>();
    await replacement.RecoverPendingAsync(CancellationToken.None);
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
        if (AdminTokenAuth.HasBearerToken(context.Request))
        {
            // Media elements cannot attach an Authorization header. Establish an HttpOnly,
            // same-site cookie from a successful bearer request so their plain content URLs stay
            // authenticated without putting the admin token in query strings or browser history.
            var forwardedScheme = context.Request.Headers["X-Forwarded-Proto"]
                .ToString()
                .Split(',', 2)[0]
                .Trim();
            var secureCookie = context.Request.IsHttps
                || string.Equals(forwardedScheme, "https", StringComparison.OrdinalIgnoreCase);
            AdminTokenAuth.SetSessionCookie(context.Response, adminToken!, secureCookie);
        }
        await next();
        return;
    }

    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
    await context.Response.WriteAsJsonAsync(
        new ApiError("auth.required", "Admin token required."),
        context.RequestAborted);
});

app.MapHealthEndpoints(adminToken, configDirectory);

app.MapSystemEndpoints();

app.MapLibraryEndpoints();

app.MapMediaAndQueueEndpoints();

app.MapStatsEndpoints(configDirectory);

app.MapReplacementEndpoints();

app.MapHub<JobsHub>("/hubs/jobs");

app.MapFallbackToFile("index.html", staticFileOptions);

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
    double VerificationMinimumVmafCatastrophicMin,
    bool VerificationAudioLoudnessGateEnabled,
    double VerificationMaxLoudnessDriftLufs,
    bool VerificationAudioClippingGateEnabled,
    double VerificationMaxTruePeakDbtp,
    bool VerificationImageQualityGateEnabled,
    double VerificationMinimumImageSsim,
    bool VerificationImageMetadataGateEnabled,
    bool VerificationClipVmafEnabled,
    int VerificationVmafFrameSubsample,
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
        settings.VerificationPolicy.MinimumVmafCatastrophicMin,
        settings.VerificationPolicy.AudioLoudnessGateEnabled,
        settings.VerificationPolicy.MaxLoudnessDriftLufs,
        settings.VerificationPolicy.AudioClippingGateEnabled,
        settings.VerificationPolicy.MaxTruePeakDbtp,
        settings.VerificationPolicy.ImageQualityGateEnabled,
        settings.VerificationPolicy.MinimumImageSsim,
        settings.VerificationPolicy.ImageMetadataGateEnabled,
        settings.VerificationPolicy.ClipVmafEnabled,
        settings.VerificationPolicy.VmafFrameSubsample,
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
    bool? SkipEfficientSources,
    string? TargetVideoCodec,
    string? TargetContainer,
    string? HdrHandling,
    bool? OptimiseDolbyVision,
    string? ExcludePaths,
    int? QualityCrf,
    string? EncoderPreset,
    string? AudioTargetCodec,
    int? AudioBitrateKbps,
    string? VideoAudioCodec,
    int? VideoAudioBitrateKbps,
    bool? DownmixToStereo,
    string? KeepAudioLanguages,
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
    bool SkipEfficientSources,
    string? TargetVideoCodec,
    string? TargetContainer,
    string? HdrHandling,
    bool OptimiseDolbyVision,
    string? ExcludePaths,
    int? QualityCrf,
    string? EncoderPreset,
    string? AudioTargetCodec,
    int? AudioBitrateKbps,
    string? VideoAudioCodec,
    int? VideoAudioBitrateKbps,
    bool DownmixToStereo,
    string? KeepAudioLanguages,
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
        library.SkipEfficientSources,
        library.TargetVideoCodec,
        library.TargetContainer,
        library.HdrHandling?.ToString(),
        library.OptimiseDolbyVision,
        library.ExcludePaths,
        library.QualityCrf,
        library.EncoderPreset,
        library.AudioTargetCodec,
        library.AudioBitrateKbps,
        library.VideoAudioCodec,
        library.VideoAudioBitrateKbps,
        library.DownmixToStereo,
        library.KeepAudioLanguages,
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
