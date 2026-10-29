using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Library;
using Optimisarr.Api.Workers;
using Optimisarr.Core.SuperResolution;
using Optimisarr.Data;

namespace Optimisarr.Tests;

/// <summary>
/// The super-resolution options must survive a restart, and an old database (or a foreign
/// backup) with no super-resolution keys at all must read back as the documented defaults.
/// </summary>
public sealed class SuperResolutionSettingsStoreTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<OptimisarrDbContext> _options;

    public SuperResolutionSettingsStoreTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<OptimisarrDbContext>().UseSqlite(_connection).Options;
        using var db = new OptimisarrDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private OptimisarrDbContext CreateDb() => new(_options);

    [Fact]
    public async Task A_fresh_database_reads_the_documented_defaults()
    {
        await using var db = CreateDb();
        var options = await new SettingsStore(db).GetSuperResolutionOptionsAsync(CancellationToken.None);

        Assert.Equal(SuperResolutionOptions.Defaults, options);
        Assert.False(options.Enabled);
        Assert.Equal("mixed", options.Preset);
        Assert.Equal(2, options.Scale);
        Assert.Equal(1, options.NoiseLevel);
        Assert.Equal("auto", options.Device);
    }

    [Fact]
    public async Task Saved_options_survive_a_restart()
    {
        await using (var db = CreateDb())
        {
            var store = new SettingsStore(db);
            await store.SetSuperResolutionOptionsAsync(
                SuperResolutionOptions.Defaults with
                {
                    Enabled = true,
                    Preset = "pro",
                    Scale = 3,
                    NoiseLevel = -1,
                    Device = "gpu:1",
                    BinaryPath = "/opt/realcugan/realcugan-ncnn-vulkan",
                    ModelsPath = "/opt/realcugan/models",
                }, CancellationToken.None);
        }

        await using var restartedDb = CreateDb();
        var read = await new SettingsStore(restartedDb).GetSuperResolutionOptionsAsync(CancellationToken.None);
        Assert.True(read.Enabled);
        Assert.Equal("pro", read.Preset);
        Assert.Equal(3, read.Scale);
        Assert.Equal(-1, read.NoiseLevel);
        Assert.Equal("gpu:1", read.Device);
        Assert.Equal("/opt/realcugan/realcugan-ncnn-vulkan", read.BinaryPath);
        Assert.Equal("/opt/realcugan/models", read.ModelsPath);
    }

    [Fact]
    public async Task Corrupt_stored_values_read_back_as_defaults_per_field()
    {
        await using var db = CreateDb();
        db.AppSettings.AddRange(
            new AppSetting { Key = SettingKeys.RealCuganScale, Value = "9" },
            new AppSetting { Key = SettingKeys.RealCuganPreset, Value = "anime" },
            new AppSetting { Key = SettingKeys.RealCuganDevice, Value = "wrapper" });
        await db.SaveChangesAsync();

        var read = await new SettingsStore(db).GetSuperResolutionOptionsAsync(CancellationToken.None);
        Assert.Equal(2, read.Scale);
        Assert.Equal("mixed", read.Preset);
        Assert.Equal("auto", read.Device);
    }

    [Fact]
    public async Task Imported_backups_without_super_resolution_keys_are_upgraded_safely()
    {
        await using var db = CreateDb();
        db.AppSettings.Add(new AppSetting { Key = SettingKeys.MaxConcurrentJobs, Value = "2" });
        await db.SaveChangesAsync();

        // An old backup carries no realcugan.* keys at all; reading them must neither throw nor
        // disturb the settings the backup does have.
        var read = await new SettingsStore(db).GetSuperResolutionOptionsAsync(CancellationToken.None);
        Assert.Equal(SuperResolutionOptions.Defaults, read);
        Assert.Equal(2, await new SettingsStore(db).GetMaxConcurrentJobsAsync(CancellationToken.None));
    }
}
