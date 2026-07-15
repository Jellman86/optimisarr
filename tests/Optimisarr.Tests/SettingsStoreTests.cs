using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Library;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Settings;
using Optimisarr.Data;

namespace Optimisarr.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<OptimisarrDbContext> _options;

    public SettingsStoreTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<OptimisarrDbContext>().UseSqlite(_connection).Options;
        using var db = new OptimisarrDbContext(_options);
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task Queue_settings_have_conservative_defaults()
    {
        await using var db = CreateDb();
        var settings = await new SettingsStore(db).GetQueueSettingsAsync(CancellationToken.None);

        Assert.Equal(1, settings.MaxConcurrentJobs);
        Assert.Equal(10L * 1024 * 1024 * 1024, settings.MinFreeDiskBytes);
        Assert.Equal(0, settings.CpuThreadLimit);
        Assert.Equal(1, settings.LibraryScanIntervalHours);
        Assert.Equal(EncoderMode.Auto, settings.EncoderMode);
        Assert.True(settings.HardwareDecode);
        Assert.Equal(1.0, settings.VerificationPolicy.DurationTolerancePercent);
        Assert.True(settings.VerificationPolicy.RequireAudioRetained);
        Assert.False(settings.VerificationPolicy.RequireSubtitlesRetained);
        Assert.True(settings.VerificationPolicy.RequireSizeReduction);
        Assert.False(settings.VerificationPolicy.QualityGateEnabled);
        Assert.Equal(93.0, settings.VerificationPolicy.MinimumVmafHarmonicMean);
        Assert.Equal(80.0, settings.VerificationPolicy.MinimumVmafMin);
        Assert.Equal(50.0, settings.VerificationPolicy.MinimumVmafCatastrophicMin);
        Assert.False(settings.VerificationPolicy.AudioLoudnessGateEnabled);
        Assert.Equal(1.0, settings.VerificationPolicy.MaxLoudnessDriftLufs);
        Assert.True(settings.VerificationPolicy.ImageQualityGateEnabled);
        Assert.Equal(0.95, settings.VerificationPolicy.MinimumImageSsim);
        Assert.True(settings.VerificationPolicy.ImageMetadataGateEnabled);
        Assert.False(settings.VerificationPolicy.ClipVmafEnabled);
        Assert.Equal(1, settings.VerificationPolicy.VmafFrameSubsample);
        Assert.False(settings.ReplacementAllowCrossFilesystem);
        Assert.False(settings.DryRunMode);
        Assert.Equal(0, settings.ReplacementQuarantineRetentionDays);
    }

    [Fact]
    public async Task Queue_settings_round_trip()
    {
        await using (var db = CreateDb())
        {
            await new SettingsStore(db).SetQueueSettingsAsync(new QueueSettings(
                MaxConcurrentJobs: 2,
                MinFreeDiskBytes: 50L * 1024 * 1024 * 1024,
                CpuThreadLimit: 2,
                LibraryScanIntervalHours: 6,
                EncoderMode: EncoderMode.NvidiaNvenc,
                HardwareDecode: false,
                VerificationPolicy: new(
                    DurationTolerancePercent: 2.5,
                    RequireAudioRetained: false,
                    RequireSubtitlesRetained: true,
                    RequireSizeReduction: false,
                    QualityGateEnabled: true,
                    MinimumVmafHarmonicMean: 92.0,
                    MinimumVmafMin: 75.0,
                    MinimumVmafCatastrophicMin: 45.0,
                    AudioLoudnessGateEnabled: true,
                    MaxLoudnessDriftLufs: 2.0,
                    AudioClippingGateEnabled: true,
                    MaxTruePeakDbtp: -1.0,
                    ImageQualityGateEnabled: true,
                    MinimumImageSsim: 0.97,
                    ImageMetadataGateEnabled: true,
                    ClipVmafEnabled: true,
                    VmafFrameSubsample: 4),
                ReplacementAllowCrossFilesystem: true,
                DryRunMode: true,
                ReplacementQuarantineRetentionDays: 30), CancellationToken.None);
        }

        await using var readDb = CreateDb();
        var settings = await new SettingsStore(readDb).GetQueueSettingsAsync(CancellationToken.None);

        Assert.Equal(2, settings.MaxConcurrentJobs);
        Assert.Equal(50L * 1024 * 1024 * 1024, settings.MinFreeDiskBytes);
        Assert.Equal(2, settings.CpuThreadLimit);
        Assert.Equal(6, settings.LibraryScanIntervalHours);
        Assert.Equal(EncoderMode.NvidiaNvenc, settings.EncoderMode);
        Assert.False(settings.HardwareDecode);
        Assert.Equal(2.5, settings.VerificationPolicy.DurationTolerancePercent);
        Assert.False(settings.VerificationPolicy.RequireAudioRetained);
        Assert.True(settings.VerificationPolicy.RequireSubtitlesRetained);
        Assert.False(settings.VerificationPolicy.RequireSizeReduction);
        Assert.False(settings.VerificationPolicy.QualityGateEnabled);
        Assert.Equal(93.0, settings.VerificationPolicy.MinimumVmafHarmonicMean);
        Assert.Equal(80.0, settings.VerificationPolicy.MinimumVmafMin);
        Assert.Equal(50.0, settings.VerificationPolicy.MinimumVmafCatastrophicMin);
        Assert.True(settings.VerificationPolicy.AudioLoudnessGateEnabled);
        Assert.Equal(2.0, settings.VerificationPolicy.MaxLoudnessDriftLufs);
        Assert.True(settings.VerificationPolicy.AudioClippingGateEnabled);
        Assert.Equal(-1.0, settings.VerificationPolicy.MaxTruePeakDbtp);
        Assert.True(settings.VerificationPolicy.ImageQualityGateEnabled);
        Assert.Equal(0.97, settings.VerificationPolicy.MinimumImageSsim);
        Assert.True(settings.VerificationPolicy.ImageMetadataGateEnabled);
        Assert.False(settings.VerificationPolicy.ClipVmafEnabled);
        Assert.Equal(1, settings.VerificationPolicy.VmafFrameSubsample);
        Assert.True(settings.ReplacementAllowCrossFilesystem);
        Assert.True(settings.DryRunMode);
        Assert.Equal(30, settings.ReplacementQuarantineRetentionDays);
        Assert.DoesNotContain(
            await readDb.AppSettings.Select(setting => setting.Key).ToListAsync(),
            key => key is "verification.qualityGateEnabled"
                or "verification.minimumVmafHarmonicMean"
                or "verification.minimumVmafMin"
                or "verification.minimumVmafCatastrophicMin"
                or "verification.clipVmafEnabled"
                or "verification.vmafFrameSubsample");
    }

    [Fact]
    public async Task Invalid_persisted_values_fall_back_to_defaults()
    {
        await using (var db = CreateDb())
        {
            db.AppSettings.AddRange(
                new AppSetting { Key = SettingKeys.MaxConcurrentJobs, Value = "0" },
                new AppSetting { Key = SettingKeys.MinFreeDiskBytes, Value = "-1" },
                new AppSetting { Key = SettingKeys.CpuThreadLimit, Value = "-2" },
                new AppSetting { Key = SettingKeys.EncoderMode, Value = "gpu-but-not-real" },
                new AppSetting { Key = SettingKeys.VerificationDurationTolerancePercent, Value = "-0.1" },
                new AppSetting { Key = SettingKeys.VerificationRequireAudioRetained, Value = "maybe" },
                new AppSetting { Key = SettingKeys.VerificationRequireSubtitlesRetained, Value = "maybe" },
                new AppSetting { Key = SettingKeys.VerificationRequireSizeReduction, Value = "maybe" },
                new AppSetting { Key = SettingKeys.ReplacementAllowCrossFilesystem, Value = "maybe" },
                new AppSetting { Key = SettingKeys.DryRunMode, Value = "maybe" },
                new AppSetting { Key = SettingKeys.ReplacementQuarantineRetentionDays, Value = "-3" });
            await db.SaveChangesAsync();
        }

        await using var readDb = CreateDb();
        var settings = await new SettingsStore(readDb).GetQueueSettingsAsync(CancellationToken.None);

        Assert.Equal(1, settings.MaxConcurrentJobs);
        Assert.Equal(10L * 1024 * 1024 * 1024, settings.MinFreeDiskBytes);
        Assert.Equal(0, settings.CpuThreadLimit);
        Assert.Equal(1, settings.LibraryScanIntervalHours);
        Assert.Equal(EncoderMode.Auto, settings.EncoderMode);
        Assert.True(settings.HardwareDecode);
        Assert.Equal(1.0, settings.VerificationPolicy.DurationTolerancePercent);
        Assert.True(settings.VerificationPolicy.RequireAudioRetained);
        Assert.False(settings.VerificationPolicy.RequireSubtitlesRetained);
        Assert.True(settings.VerificationPolicy.RequireSizeReduction);
        Assert.Equal(1, settings.VerificationPolicy.VmafFrameSubsample);
        Assert.False(settings.ReplacementAllowCrossFilesystem);
        Assert.False(settings.DryRunMode);
        Assert.Equal(0, settings.ReplacementQuarantineRetentionDays);
    }

    [Fact]
    public async Task Setup_state_initialisation_distinguishes_a_fresh_install_from_an_upgrade()
    {
        await using (var freshDb = CreateDb())
        {
            var state = await new SettingsStore(freshDb).InitialiseSetupStateAsync(
                databaseExistedBeforeStartup: false,
                CancellationToken.None);
            Assert.Equal(SetupState.Pending, state);
            Assert.True((await new SettingsStore(freshDb).GetQueueSettingsAsync(CancellationToken.None)).DryRunMode);
        }

        await using (var retainedDb = CreateDb())
        {
            var retained = await new SettingsStore(retainedDb).InitialiseSetupStateAsync(
                databaseExistedBeforeStartup: true,
                CancellationToken.None);
            Assert.Equal(SetupState.Pending, retained);
        }

        await using (var upgradeDb = CreateDb())
        {
            upgradeDb.AppSettings.RemoveRange(upgradeDb.AppSettings);
            await upgradeDb.SaveChangesAsync();
            var upgrade = await new SettingsStore(upgradeDb).InitialiseSetupStateAsync(
                databaseExistedBeforeStartup: true,
                CancellationToken.None);
            Assert.Equal(SetupState.CompletedUpgrade, upgrade);
            Assert.False((await new SettingsStore(upgradeDb).GetQueueSettingsAsync(CancellationToken.None)).DryRunMode);
        }
    }

    [Fact]
    public async Task Setup_progress_round_trips_and_can_be_restarted()
    {
        var progressed = SetupState.Pending.Advance(1).Advance(2);
        await using (var db = CreateDb())
        {
            await new SettingsStore(db).SetSetupStateAsync(progressed, CancellationToken.None);
        }

        await using (var readDb = CreateDb())
        {
            var store = new SettingsStore(readDb);
            Assert.Equal(progressed, await store.GetSetupStateAsync(CancellationToken.None));
            await store.SetSetupStateAsync(progressed.Restart(), CancellationToken.None);
        }

        await using var restartedDb = CreateDb();
        Assert.Equal(SetupState.Pending, await new SettingsStore(restartedDb).GetSetupStateAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Inconsistent_setup_state_fails_safe_as_an_already_running_installation()
    {
        await using var db = CreateDb();
        db.AppSettings.Add(new AppSetting
        {
            Key = SettingKeys.SetupState,
            Value = """{"Version":1,"CompletedStep":0,"Completed":true}"""
        });
        await db.SaveChangesAsync();

        Assert.Equal(SetupState.CompletedUpgrade, await new SettingsStore(db).GetSetupStateAsync(CancellationToken.None));
    }

    private OptimisarrDbContext CreateDb() => new(_options);

    public void Dispose() => _connection.Dispose();
}
