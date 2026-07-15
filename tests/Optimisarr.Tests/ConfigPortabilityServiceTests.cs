using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Library;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Settings;
using Optimisarr.Data;

namespace Optimisarr.Tests;

public sealed class ConfigPortabilityServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<OptimisarrDbContext> _options;
    private readonly TimeProvider _clock = TimeProvider.System;

    public ConfigPortabilityServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:;Foreign Keys=True");
        _connection.Open();
        _options = new DbContextOptionsBuilder<OptimisarrDbContext>().UseSqlite(_connection).Options;
        using var db = new OptimisarrDbContext(_options);
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task Export_omits_secrets_and_includes_settings_and_definitions()
    {
        await using (var db = CreateDb())
        {
            db.AppSettings.Add(new AppSetting { Key = SettingKeys.MaxConcurrentJobs, Value = "3" });
            db.AppSettings.Add(new AppSetting { Key = SettingKeys.PlexClientIdentifier, Value = "secret-id" });
            db.Libraries.Add(new Library { Name = "Films", Path = "/data/films", MediaType = MediaType.Film });
            db.ActivityWatchers.Add(new ActivityWatcher
            {
                Name = "Plex", Type = ActivityWatcherType.Plex, BaseUrl = "http://plex:32400", ApiToken = "tok"
            });
            db.NotificationTargets.Add(new NotificationTarget
            {
                Name = "ntfy", Type = NotificationType.Ntfy, Url = "https://ntfy.sh/x", Token = "tok"
            });
            await db.SaveChangesAsync();
        }

        var snapshot = await ExportAsync();

        Assert.Equal(ConfigSnapshot.CurrentVersion, snapshot.Version);
        Assert.Equal("3", snapshot.Settings[SettingKeys.MaxConcurrentJobs]);
        Assert.False(snapshot.Settings.ContainsKey(SettingKeys.PlexClientIdentifier));
        Assert.Equal("/data/films", Assert.Single(snapshot.Libraries).Path);
        Assert.Equal("Plex", Assert.Single(snapshot.ActivityWatchers).Name);
        Assert.Equal("ntfy", Assert.Single(snapshot.NotificationTargets).Name);
    }

    [Fact]
    public async Task Import_creates_new_definitions_and_applies_settings()
    {
        var snapshot = new ConfigSnapshot(
            ConfigSnapshot.CurrentVersion,
            _clock.GetUtcNow(),
            new Dictionary<string, string> { [SettingKeys.MaxConcurrentJobs] = "4" },
            [new LibrarySnapshot("Films", "/data/films", "Film", "ConservativeHevc", true, 0,
                null, null, null, null, null, null, null, null, false, null)],
            [new ActivityWatcherSnapshot("Plex", "Plex", "http://plex:32400", true, true)],
            [new NotificationTargetSnapshot("ntfy", "Ntfy", "https://ntfy.sh/x", true, true, true)],
            [new ArrConnectionSnapshot("Radarr", "Radarr", "http://radarr:7878", true)]);

        var result = await ImportAsync(snapshot);

        Assert.True(result.Applied);
        Assert.Equal(1, result.LibrariesCreated);
        Assert.Equal(1, result.WatchersCreated);
        Assert.Equal(1, result.TargetsCreated);
        Assert.Equal(1, result.ArrConnectionsCreated);
        Assert.Equal(1, result.SettingsApplied);

        await using var db = CreateDb();
        Assert.Equal(4, await new SettingsStore(db).GetMaxConcurrentJobsAsync(CancellationToken.None));
        Assert.Equal("/data/films", (await db.Libraries.SingleAsync()).Path);
    }

    [Fact]
    public async Task Import_updates_a_matching_library_without_creating_a_duplicate()
    {
        await using (var db = CreateDb())
        {
            db.Libraries.Add(new Library { Name = "Old name", Path = "/data/films", Priority = 0 });
            await db.SaveChangesAsync();
        }

        var snapshot = EmptySnapshot() with
        {
            Libraries = [new LibrarySnapshot("New name", "/data/films", "Film", "ConservativeHevc", true, 7,
                null, null, null, null, null, null, null, null, false, null)]
        };

        var result = await ImportAsync(snapshot);

        Assert.Equal(0, result.LibrariesCreated);
        Assert.Equal(1, result.LibrariesUpdated);

        await using var readDb = CreateDb();
        var library = await readDb.Libraries.SingleAsync();
        Assert.Equal("New name", library.Name);
        Assert.Equal(7, library.Priority);
    }

    [Fact]
    public async Task Import_preserves_an_existing_token_because_a_snapshot_carries_none()
    {
        await using (var db = CreateDb())
        {
            db.NotificationTargets.Add(new NotificationTarget
            {
                Name = "ntfy", Type = NotificationType.Ntfy, Url = "https://ntfy.sh/old", Token = "keep-me"
            });
            await db.SaveChangesAsync();
        }

        var snapshot = EmptySnapshot() with
        {
            NotificationTargets = [new NotificationTargetSnapshot("ntfy", "Ntfy", "https://ntfy.sh/new", true, true, true)]
        };

        await ImportAsync(snapshot);

        await using var readDb = CreateDb();
        var target = await readDb.NotificationTargets.SingleAsync();
        Assert.Equal("https://ntfy.sh/new", target.Url);
        Assert.Equal("keep-me", target.Token);
    }

    [Fact]
    public async Task Import_rejects_an_invalid_snapshot_and_writes_nothing()
    {
        var snapshot = EmptySnapshot() with { Version = ConfigSnapshot.CurrentVersion + 1 };

        var result = await ImportAsync(snapshot);

        Assert.False(result.Applied);
        Assert.NotEmpty(result.Errors);

        await using var db = CreateDb();
        Assert.Equal(0, await db.Libraries.CountAsync());
    }

    [Fact]
    public async Task Import_rejects_invalid_image_and_window_fields_instead_of_coercing_them()
    {
        var snapshot = EmptySnapshot() with
        {
            Libraries =
            [
                new LibrarySnapshot("Photos", "/data/photos", "Other", "ConservativeHevc", true, 0,
                    null, null, null, null, null, null, null, null, false, null,
                    ImageDownscaleMode: "Impossible",
                    AutoEnqueueWindowStart: "bad")
            ]
        };

        var result = await ImportAsync(snapshot);

        Assert.False(result.Applied);
        Assert.Contains(result.Errors, error => error.Contains("image downscale mode", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, error => error.Contains("auto-enqueue window start", StringComparison.OrdinalIgnoreCase));

        await using var db = CreateDb();
        Assert.Equal(0, await db.Libraries.CountAsync());
    }

    [Fact]
    public async Task Import_migrates_a_legacy_avif_target_to_webp()
    {
        var snapshot = EmptySnapshot() with
        {
            Libraries =
            [
                new LibrarySnapshot("Photos", "/data/photos", "Photo", "ConservativeHevc", true, 0,
                    null, null, null, null, null, null, null, null, false, null,
                    TargetImageFormat: "AVIF")
            ]
        };

        var result = await ImportAsync(snapshot);

        Assert.True(result.Applied);
        await using var db = CreateDb();
        Assert.Equal("webp", (await db.Libraries.SingleAsync()).TargetImageFormat);
    }

    [Fact]
    public async Task Import_materialises_legacy_global_vmaf_settings_without_persisting_them()
    {
        var snapshot = EmptySnapshot() with
        {
            Settings = new Dictionary<string, string>
            {
                ["verification.qualityGateEnabled"] = "True",
                ["verification.minimumVmafHarmonicMean"] = "88",
                ["verification.minimumVmafMin"] = "72",
                ["verification.minimumVmafCatastrophicMin"] = "42",
                ["verification.clipVmafEnabled"] = "True",
                ["verification.vmafFrameSubsample"] = "3"
            },
            Libraries =
            [
                new LibrarySnapshot("Films", "/data/films", "Film", "ConservativeHevc", true, 0,
                    null, null, null, null, null, null, null, null, false, null),
                new LibrarySnapshot("Archive", "/data/archive", "Film", "ConservativeHevc", true, 0,
                    null, null, null, null, null, null, null, null, false, null,
                    VmafQualityGateEnabled: false,
                    MinVmafHarmonicMean: 96,
                    MinVmafMin: 90,
                    MinVmafCatastrophicMin: 70,
                    ClipVmafEnabled: false,
                    VmafFrameSubsample: 1)
            ]
        };

        var result = await ImportAsync(snapshot);

        Assert.True(result.Applied);
        Assert.Equal(0, result.SettingsApplied);
        await using var db = CreateDb();
        var films = await db.Libraries.SingleAsync(library => library.Path == "/data/films");
        Assert.True(films.VmafQualityGateEnabled);
        Assert.Equal(88, films.MinVmafHarmonicMean);
        Assert.Equal(72, films.MinVmafMin);
        Assert.Equal(42, films.MinVmafCatastrophicMin);
        Assert.True(films.ClipVmafEnabled);
        Assert.Equal(3, films.VmafFrameSubsample);

        var archive = await db.Libraries.SingleAsync(library => library.Path == "/data/archive");
        Assert.False(archive.VmafQualityGateEnabled);
        Assert.Equal(96, archive.MinVmafHarmonicMean);
        Assert.Equal(90, archive.MinVmafMin);
        Assert.Equal(70, archive.MinVmafCatastrophicMin);
        Assert.False(archive.ClipVmafEnabled);
        Assert.Equal(1, archive.VmafFrameSubsample);
        Assert.Empty(await db.AppSettings.ToListAsync());
    }

    [Fact]
    public async Task Exported_config_round_trips_back_through_import()
    {
        await using (var db = CreateDb())
        {
            db.AppSettings.AddRange(
                new AppSetting { Key = SettingKeys.EncoderMode, Value = "NvidiaNvenc" });
            db.Libraries.Add(new Library
            {
                Name = "TV", Path = "/data/tv", MediaType = MediaType.Tv,
                RuleProfile = RuleProfile.CompatibilityH264, Priority = 2, MaxHeight = 1080,
                VideoAudioCodec = "aac", VideoAudioBitrateKbps = 160, DownmixToStereo = true,
                KeepAudioLanguages = " ENG, jpn, eng ",
                ReencodeLossyAudio = true,
                VmafQualityGateEnabled = true,
                MinVmafHarmonicMean = 91,
                MinVmafMin = 78,
                MinVmafCatastrophicMin = 48,
                ClipVmafEnabled = true,
                VmafFrameSubsample = 2,
                AutoEnqueueEnabled = true,
                AutoEnqueueWindowStart = new TimeOnly(1, 0),
                AutoEnqueueWindowEnd = new TimeOnly(6, 30)
            });
            db.ActivityWatchers.Add(new ActivityWatcher
            {
                Name = "Plex", Type = ActivityWatcherType.Plex, BaseUrl = "http://plex:32400", ApiToken = "plex-secret"
            });
            db.NotificationTargets.Add(new NotificationTarget
            {
                Name = "ntfy", Type = NotificationType.Ntfy, Url = "https://ntfy.sh/optimisarr", Token = "notify-secret"
            });
            db.ArrConnections.Add(new ArrConnection
            {
                Name = "Radarr", Type = ArrConnectionType.Radarr, BaseUrl = "http://radarr:7878", ApiKey = "arr-secret"
            });
            await db.SaveChangesAsync();
        }

        var exported = await ExportAsync();

        // A fresh database imports the snapshot to the same shape.
        await ResetDatabaseAsync();
        var result = await ImportAsync(exported);

        Assert.True(result.Applied);
        await using var db2 = CreateDb();
        var library = await db2.Libraries.SingleAsync();
        Assert.Equal("/data/tv", library.Path);
        Assert.Equal(MediaType.Tv, library.MediaType);
        Assert.Equal(RuleProfile.CompatibilityH264, library.RuleProfile);
        Assert.Equal(1080, library.MaxHeight);
        Assert.Equal("aac", library.VideoAudioCodec);
        Assert.Equal(160, library.VideoAudioBitrateKbps);
        Assert.True(library.DownmixToStereo);
        Assert.Equal("eng, jpn", library.KeepAudioLanguages);
        Assert.True(library.ReencodeLossyAudio);
        Assert.True(library.VmafQualityGateEnabled);
        Assert.Equal(91, library.MinVmafHarmonicMean);
        Assert.Equal(78, library.MinVmafMin);
        Assert.Equal(48, library.MinVmafCatastrophicMin);
        Assert.True(library.ClipVmafEnabled);
        Assert.Equal(2, library.VmafFrameSubsample);
        Assert.True(library.AutoEnqueueEnabled);
        Assert.Equal(new TimeOnly(1, 0), library.AutoEnqueueWindowStart);
        Assert.Equal(new TimeOnly(6, 30), library.AutoEnqueueWindowEnd);
        Assert.Equal(EncoderMode.NvidiaNvenc, (await new SettingsStore(db2).GetQueueSettingsAsync(CancellationToken.None)).EncoderMode);
        Assert.Equal("plex-secret", (await db2.ActivityWatchers.SingleAsync()).ApiToken);
        Assert.Equal("notify-secret", (await db2.NotificationTargets.SingleAsync()).Token);
        Assert.Equal("arr-secret", (await db2.ArrConnections.SingleAsync()).ApiKey);
    }

    private async Task ResetDatabaseAsync()
    {
        await using var db = CreateDb();
        db.Libraries.RemoveRange(db.Libraries);
        db.ActivityWatchers.RemoveRange(db.ActivityWatchers);
        db.NotificationTargets.RemoveRange(db.NotificationTargets);
        db.AppSettings.RemoveRange(db.AppSettings);
        await db.SaveChangesAsync();
    }

    private async Task<ConfigSnapshot> ExportAsync()
    {
        await using var db = CreateDb();
        return await new ConfigPortabilityService(db, new SettingsStore(db), _clock).ExportAsync(CancellationToken.None);
    }

    private async Task<ConfigImportResult> ImportAsync(ConfigSnapshot snapshot)
    {
        await using var db = CreateDb();
        return await new ConfigPortabilityService(db, new SettingsStore(db), _clock).ImportAsync(snapshot, CancellationToken.None);
    }

    private static ConfigSnapshot EmptySnapshot() => new(
        ConfigSnapshot.CurrentVersion,
        DateTimeOffset.UnixEpoch,
        new Dictionary<string, string>(),
        [],
        [],
        [],
        []);

    private OptimisarrDbContext CreateDb() => new(_options);

    public void Dispose() => _connection.Dispose();
}
