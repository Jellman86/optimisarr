using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Library;
using Optimisarr.Core.Queue;
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
        Assert.False(settings.VerificationPolicy.AudioLoudnessGateEnabled);
        Assert.Equal(1.0, settings.VerificationPolicy.MaxLoudnessDriftLufs);
        Assert.False(settings.VerificationPolicy.ImageQualityGateEnabled);
        Assert.Equal(0.95, settings.VerificationPolicy.MinimumImageSsim);
        Assert.False(settings.VerificationPolicy.ImageMetadataGateEnabled);
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
                    AudioLoudnessGateEnabled: true,
                    MaxLoudnessDriftLufs: 2.0,
                    AudioClippingGateEnabled: true,
                    MaxTruePeakDbtp: -1.0,
                    ImageQualityGateEnabled: true,
                    MinimumImageSsim: 0.97,
                    ImageMetadataGateEnabled: true),
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
        Assert.True(settings.VerificationPolicy.QualityGateEnabled);
        Assert.Equal(92.0, settings.VerificationPolicy.MinimumVmafHarmonicMean);
        Assert.Equal(75.0, settings.VerificationPolicy.MinimumVmafMin);
        Assert.True(settings.VerificationPolicy.AudioLoudnessGateEnabled);
        Assert.Equal(2.0, settings.VerificationPolicy.MaxLoudnessDriftLufs);
        Assert.True(settings.VerificationPolicy.AudioClippingGateEnabled);
        Assert.Equal(-1.0, settings.VerificationPolicy.MaxTruePeakDbtp);
        Assert.True(settings.VerificationPolicy.ImageQualityGateEnabled);
        Assert.Equal(0.97, settings.VerificationPolicy.MinimumImageSsim);
        Assert.True(settings.VerificationPolicy.ImageMetadataGateEnabled);
        Assert.True(settings.ReplacementAllowCrossFilesystem);
        Assert.True(settings.DryRunMode);
        Assert.Equal(30, settings.ReplacementQuarantineRetentionDays);
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
        Assert.False(settings.ReplacementAllowCrossFilesystem);
        Assert.False(settings.DryRunMode);
        Assert.Equal(0, settings.ReplacementQuarantineRetentionDays);
    }

    private OptimisarrDbContext CreateDb() => new(_options);

    public void Dispose() => _connection.Dispose();
}
