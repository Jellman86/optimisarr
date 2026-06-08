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
        Assert.False(settings.ScheduleEnabled);
        Assert.Equal(new TimeOnly(0, 0), settings.ScheduleWindowStart);
        Assert.Equal(new TimeOnly(0, 0), settings.ScheduleWindowEnd);
        Assert.Equal(10L * 1024 * 1024 * 1024, settings.MinFreeDiskBytes);
        Assert.Equal(0, settings.CpuThreadLimit);
        Assert.Equal(EncoderMode.Auto, settings.EncoderMode);
        Assert.Equal(1.0, settings.VerificationPolicy.DurationTolerancePercent);
        Assert.True(settings.VerificationPolicy.RequireAudioRetained);
        Assert.False(settings.VerificationPolicy.RequireSubtitlesRetained);
        Assert.True(settings.VerificationPolicy.RequireSizeReduction);
    }

    [Fact]
    public async Task Queue_settings_round_trip()
    {
        await using (var db = CreateDb())
        {
            await new SettingsStore(db).SetQueueSettingsAsync(new QueueSettings(
                MaxConcurrentJobs: 2,
                ScheduleEnabled: true,
                ScheduleWindowStart: new TimeOnly(22, 0),
                ScheduleWindowEnd: new TimeOnly(6, 30),
                MinFreeDiskBytes: 50L * 1024 * 1024 * 1024,
                CpuThreadLimit: 2,
                EncoderMode: EncoderMode.NvidiaNvenc,
                VerificationPolicy: new(
                    DurationTolerancePercent: 2.5,
                    RequireAudioRetained: false,
                    RequireSubtitlesRetained: true,
                    RequireSizeReduction: false)), CancellationToken.None);
        }

        await using var readDb = CreateDb();
        var settings = await new SettingsStore(readDb).GetQueueSettingsAsync(CancellationToken.None);

        Assert.Equal(2, settings.MaxConcurrentJobs);
        Assert.True(settings.ScheduleEnabled);
        Assert.Equal(new TimeOnly(22, 0), settings.ScheduleWindowStart);
        Assert.Equal(new TimeOnly(6, 30), settings.ScheduleWindowEnd);
        Assert.Equal(50L * 1024 * 1024 * 1024, settings.MinFreeDiskBytes);
        Assert.Equal(2, settings.CpuThreadLimit);
        Assert.Equal(EncoderMode.NvidiaNvenc, settings.EncoderMode);
        Assert.Equal(2.5, settings.VerificationPolicy.DurationTolerancePercent);
        Assert.False(settings.VerificationPolicy.RequireAudioRetained);
        Assert.True(settings.VerificationPolicy.RequireSubtitlesRetained);
        Assert.False(settings.VerificationPolicy.RequireSizeReduction);
    }

    [Fact]
    public async Task Invalid_persisted_values_fall_back_to_defaults()
    {
        await using (var db = CreateDb())
        {
            db.AppSettings.AddRange(
                new AppSetting { Key = SettingKeys.MaxConcurrentJobs, Value = "0" },
                new AppSetting { Key = SettingKeys.ScheduleEnabled, Value = "not-a-bool" },
                new AppSetting { Key = SettingKeys.ScheduleWindowStart, Value = "25:99" },
                new AppSetting { Key = SettingKeys.ScheduleWindowEnd, Value = "6pm" },
                new AppSetting { Key = SettingKeys.MinFreeDiskBytes, Value = "-1" },
                new AppSetting { Key = SettingKeys.CpuThreadLimit, Value = "-2" },
                new AppSetting { Key = SettingKeys.EncoderMode, Value = "gpu-but-not-real" },
                new AppSetting { Key = SettingKeys.VerificationDurationTolerancePercent, Value = "-0.1" },
                new AppSetting { Key = SettingKeys.VerificationRequireAudioRetained, Value = "maybe" },
                new AppSetting { Key = SettingKeys.VerificationRequireSubtitlesRetained, Value = "maybe" },
                new AppSetting { Key = SettingKeys.VerificationRequireSizeReduction, Value = "maybe" });
            await db.SaveChangesAsync();
        }

        await using var readDb = CreateDb();
        var settings = await new SettingsStore(readDb).GetQueueSettingsAsync(CancellationToken.None);

        Assert.Equal(1, settings.MaxConcurrentJobs);
        Assert.False(settings.ScheduleEnabled);
        Assert.Equal(new TimeOnly(0, 0), settings.ScheduleWindowStart);
        Assert.Equal(new TimeOnly(0, 0), settings.ScheduleWindowEnd);
        Assert.Equal(10L * 1024 * 1024 * 1024, settings.MinFreeDiskBytes);
        Assert.Equal(0, settings.CpuThreadLimit);
        Assert.Equal(EncoderMode.Auto, settings.EncoderMode);
        Assert.Equal(1.0, settings.VerificationPolicy.DurationTolerancePercent);
        Assert.True(settings.VerificationPolicy.RequireAudioRetained);
        Assert.False(settings.VerificationPolicy.RequireSubtitlesRetained);
        Assert.True(settings.VerificationPolicy.RequireSizeReduction);
    }

    private OptimisarrDbContext CreateDb() => new(_options);

    public void Dispose() => _connection.Dispose();
}
