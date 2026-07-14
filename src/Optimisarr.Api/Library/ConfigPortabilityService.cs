using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Settings;
using Optimisarr.Data;

// This namespace's leaf segment shadows the Library entity type, so refer to it
// through this alias throughout the file.
using LibraryEntity = Optimisarr.Data.Library;

namespace Optimisarr.Api.Library;

/// <summary>What an import changed. <see cref="Applied"/> is false when validation rejected the file.</summary>
public sealed record ConfigImportResult(
    bool Applied,
    IReadOnlyList<string> Errors,
    int LibrariesCreated,
    int LibrariesUpdated,
    int WatchersCreated,
    int WatchersUpdated,
    int TargetsCreated,
    int TargetsUpdated,
    int ArrConnectionsCreated,
    int ArrConnectionsUpdated,
    int SettingsApplied);

/// <summary>
/// Exports and imports Optimisarr's configuration as a secret-bearing
/// <see cref="ConfigSnapshot"/>. Import is validated in full first (nothing is
/// written if any part is invalid), then applied as a non-destructive merge:
/// libraries are matched on path, watchers and targets on name, so importing never
/// deletes existing configuration. The exported file contains credentials and must
/// be treated as sensitive backup material.
/// </summary>
public sealed class ConfigPortabilityService(OptimisarrDbContext db, SettingsStore settings, TimeProvider clock)
{
    public async Task<ConfigSnapshot> ExportAsync(CancellationToken cancellationToken)
    {
        var settingsMap = await settings.ExportSettingsAsync(cancellationToken);

        var libraries = await db.Libraries.AsNoTracking()
            .OrderBy(library => library.Name).ToListAsync(cancellationToken);
        var watchers = await db.ActivityWatchers.AsNoTracking()
            .OrderBy(watcher => watcher.Name).ToListAsync(cancellationToken);
        var targets = await db.NotificationTargets.AsNoTracking()
            .OrderBy(target => target.Name).ToListAsync(cancellationToken);
        var arrConnections = await db.ArrConnections.AsNoTracking()
            .OrderBy(connection => connection.Name).ToListAsync(cancellationToken);

        return new ConfigSnapshot(
            ConfigSnapshot.CurrentVersion,
            clock.GetUtcNow(),
            settingsMap,
            libraries.Select(ToSnapshot).ToList(),
            watchers.Select(ToSnapshot).ToList(),
            targets.Select(ToSnapshot).ToList(),
            arrConnections.Select(ToSnapshot).ToList());
    }

    public async Task<ConfigImportResult> ImportAsync(ConfigSnapshot snapshot, CancellationToken cancellationToken)
    {
        var validation = ConfigSnapshotValidator.Validate(snapshot, SettingsStore.PortableSettingKeys);
        if (!validation.IsValid)
        {
            return new ConfigImportResult(false, validation.Errors, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var (librariesCreated, librariesUpdated) = await ImportLibrariesAsync(snapshot.Libraries, cancellationToken);
        var (watchersCreated, watchersUpdated) = await ImportWatchersAsync(snapshot.ActivityWatchers, cancellationToken);
        var (targetsCreated, targetsUpdated) = await ImportTargetsAsync(snapshot.NotificationTargets, cancellationToken);
        var (arrCreated, arrUpdated) = await ImportArrConnectionsAsync(snapshot.ArrConnections, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        var settingsApplied = await settings.ImportSettingsAsync(snapshot.Settings, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new ConfigImportResult(
            true, [],
            librariesCreated, librariesUpdated,
            watchersCreated, watchersUpdated,
            targetsCreated, targetsUpdated,
            arrCreated, arrUpdated,
            settingsApplied);
    }

    private async Task<(int Created, int Updated)> ImportLibrariesAsync(
        IReadOnlyList<LibrarySnapshot> snapshots, CancellationToken cancellationToken)
    {
        var created = 0;
        var updated = 0;
        foreach (var snapshot in snapshots)
        {
            var path = snapshot.Path.Trim();
            var library = await db.Libraries.FirstOrDefaultAsync(l => l.Path == path, cancellationToken);
            if (library is null)
            {
                library = new LibraryEntity { Path = path };
                db.Libraries.Add(library);
                created++;
            }
            else
            {
                library.UpdatedAt = clock.GetUtcNow();
                updated++;
            }

            library.Name = snapshot.Name.Trim();
            library.MediaType = ParseEnum<MediaType>(snapshot.MediaType);
            library.RuleProfile = ParseEnum<RuleProfile>(snapshot.RuleProfile);
            library.Enabled = snapshot.Enabled;
            library.Priority = snapshot.Priority;
            library.MinFileSizeBytes = snapshot.MinFileSizeBytes;
            library.MaxHeight = snapshot.MaxHeight;
            library.SkipEfficientSources = snapshot.SkipEfficientSources ?? true;
            library.TargetVideoCodec = snapshot.TargetVideoCodec;
            library.TargetContainer = snapshot.TargetContainer;
            library.HdrHandling = snapshot.HdrHandling is null ? null : ParseEnum<HdrHandling>(snapshot.HdrHandling);
            library.OptimiseDolbyVision = snapshot.OptimiseDolbyVision ?? false;
            library.ExcludePaths = snapshot.ExcludePaths;
            library.QualityCrf = snapshot.QualityCrf;
            library.EncoderPreset = snapshot.EncoderPreset;
            library.AudioTargetCodec = snapshot.AudioTargetCodec;
            library.AudioBitrateKbps = snapshot.AudioBitrateKbps;
            library.VideoAudioCodec = snapshot.VideoAudioCodec;
            library.VideoAudioBitrateKbps = snapshot.VideoAudioBitrateKbps;
            library.DownmixToStereo = snapshot.DownmixToStereo;
            library.KeepAudioLanguages = snapshot.KeepAudioLanguages;
            library.ReencodeLossyAudio = snapshot.ReencodeLossyAudio;
            // Configs exported before AVIF was withdrawn remain importable; move the stale target
            // to the same proven WebP fallback as the database migration.
            library.TargetImageFormat = snapshot.TargetImageFormat?.Equals(
                "avif", StringComparison.OrdinalIgnoreCase) == true
                ? "webp"
                : snapshot.TargetImageFormat;
            library.ImageQuality = snapshot.ImageQuality;
            library.ReencodeLossyImages = snapshot.ReencodeLossyImages;
            library.ImageDownscaleMode = ParseEnum<ImageDownscaleMode>(snapshot.ImageDownscaleMode);
            library.ImageDownscaleValue = snapshot.ImageDownscaleValue;
            library.MoveOnComplete = snapshot.MoveOnComplete;
            library.TargetFolder = snapshot.TargetFolder;
            library.MoveOverwrite = snapshot.MoveOverwrite;
            library.MinVmafHarmonicMean = snapshot.MinVmafHarmonicMean;
            library.MinVmafMin = snapshot.MinVmafMin;
            library.AutoEnqueueEnabled = snapshot.AutoEnqueueEnabled;
            library.AutoEnqueueWindowStart = ParseWindowTime(snapshot.AutoEnqueueWindowStart);
            library.AutoEnqueueWindowEnd = ParseWindowTime(snapshot.AutoEnqueueWindowEnd);
            library.AutoReplace = snapshot.AutoReplace;
        }

        return (created, updated);
    }

    private static TimeOnly ParseWindowTime(string value) =>
        TimeOnly.ParseExact(value, "HH:mm", CultureInfo.InvariantCulture);

    private async Task<(int Created, int Updated)> ImportWatchersAsync(
        IReadOnlyList<ActivityWatcherSnapshot> snapshots, CancellationToken cancellationToken)
    {
        var created = 0;
        var updated = 0;
        foreach (var snapshot in snapshots)
        {
            var name = snapshot.Name.Trim();
            var watcher = await db.ActivityWatchers.FirstOrDefaultAsync(w => w.Name == name, cancellationToken);
            if (watcher is null)
            {
                watcher = new ActivityWatcher { Name = name };
                db.ActivityWatchers.Add(watcher);
                created++;
            }
            else
            {
                watcher.UpdatedAt = clock.GetUtcNow();
                updated++;
            }

            watcher.Type = ParseEnum<ActivityWatcherType>(snapshot.Type);
            watcher.BaseUrl = snapshot.BaseUrl.Trim();
            if (snapshot.ApiToken is not null)
            {
                watcher.ApiToken = snapshot.ApiToken;
            }
            watcher.Enabled = snapshot.Enabled;
            watcher.RefreshOnReplace = snapshot.RefreshOnReplace;
        }

        return (created, updated);
    }

    private async Task<(int Created, int Updated)> ImportTargetsAsync(
        IReadOnlyList<NotificationTargetSnapshot> snapshots, CancellationToken cancellationToken)
    {
        var created = 0;
        var updated = 0;
        foreach (var snapshot in snapshots)
        {
            var name = snapshot.Name.Trim();
            var target = await db.NotificationTargets.FirstOrDefaultAsync(t => t.Name == name, cancellationToken);
            if (target is null)
            {
                target = new NotificationTarget { Name = name };
                db.NotificationTargets.Add(target);
                created++;
            }
            else
            {
                target.UpdatedAt = clock.GetUtcNow();
                updated++;
            }

            target.Type = ParseEnum<NotificationType>(snapshot.Type);
            target.Url = snapshot.Url.Trim();
            if (snapshot.Token is not null)
            {
                target.Token = snapshot.Token;
            }
            target.Enabled = snapshot.Enabled;
            target.NotifyOnReplacement = snapshot.NotifyOnReplacement;
            target.NotifyOnFailure = snapshot.NotifyOnFailure;
        }

        return (created, updated);
    }

    private async Task<(int Created, int Updated)> ImportArrConnectionsAsync(
        IReadOnlyList<ArrConnectionSnapshot> snapshots, CancellationToken cancellationToken)
    {
        var created = 0;
        var updated = 0;
        foreach (var snapshot in snapshots)
        {
            var name = snapshot.Name.Trim();
            var connection = await db.ArrConnections.FirstOrDefaultAsync(c => c.Name == name, cancellationToken);
            if (connection is null)
            {
                connection = new ArrConnection { Name = name };
                db.ArrConnections.Add(connection);
                created++;
            }
            else
            {
                connection.UpdatedAt = clock.GetUtcNow();
                updated++;
            }

            connection.Type = ParseEnum<ArrConnectionType>(snapshot.Type);
            connection.BaseUrl = snapshot.BaseUrl.Trim();
            if (snapshot.ApiKey is not null)
            {
                connection.ApiKey = snapshot.ApiKey;
            }
            connection.Enabled = snapshot.Enabled;
        }

        return (created, updated);
    }

    private static LibrarySnapshot ToSnapshot(LibraryEntity library) => new(
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
        library.MinVmafHarmonicMean,
        library.MinVmafMin,
        library.AutoEnqueueEnabled,
        library.AutoEnqueueWindowStart.ToString("HH:mm", CultureInfo.InvariantCulture),
        library.AutoEnqueueWindowEnd.ToString("HH:mm", CultureInfo.InvariantCulture),
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
        library.MoveOverwrite,
        library.AutoReplace,
        library.SkipEfficientSources,
        library.OptimiseDolbyVision,
        library.KeepAudioLanguages);

    private static ActivityWatcherSnapshot ToSnapshot(ActivityWatcher watcher) => new(
        watcher.Name,
        watcher.Type.ToString(),
        watcher.BaseUrl,
        watcher.Enabled,
        watcher.RefreshOnReplace,
        watcher.ApiToken);

    private static NotificationTargetSnapshot ToSnapshot(NotificationTarget target) => new(
        target.Name,
        target.Type.ToString(),
        target.Url,
        target.Enabled,
        target.NotifyOnReplacement,
        target.NotifyOnFailure,
        target.Token);

    private static ArrConnectionSnapshot ToSnapshot(ArrConnection connection) => new(
        connection.Name,
        connection.Type.ToString(),
        connection.BaseUrl,
        connection.Enabled,
        connection.ApiKey);

    // Snapshots are validated before import, so these parses cannot fail here.
    private static T ParseEnum<T>(string value) where T : struct, Enum =>
        Enum.Parse<T>(value, ignoreCase: true);
}
