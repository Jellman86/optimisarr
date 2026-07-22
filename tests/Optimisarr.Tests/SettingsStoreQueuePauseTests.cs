using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Library;
using Optimisarr.Data;

namespace Optimisarr.Tests;

// The manual pause must survive a container restart: an operator who paused the queue to keep
// the server free expects it to stay paused until they resume it, not to resume on reboot.
public sealed class SettingsStoreQueuePauseTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<OptimisarrDbContext> _options;

    public SettingsStoreQueuePauseTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<OptimisarrDbContext>().UseSqlite(_connection).Options;
        using var db = new OptimisarrDbContext(_options);
        db.Database.EnsureCreated();
    }

    private OptimisarrDbContext CreateDb() => new(_options);

    [Fact]
    public async Task The_queue_is_not_paused_by_default()
    {
        await using var db = CreateDb();

        Assert.False(await new SettingsStore(db).GetQueuePausedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_persisted_pause_survives_a_reload_and_can_be_cleared()
    {
        await using (var db = CreateDb())
        {
            await new SettingsStore(db).SetQueuePausedAsync(true, CancellationToken.None);
        }

        await using (var db = CreateDb())
        {
            Assert.True(await new SettingsStore(db).GetQueuePausedAsync(CancellationToken.None));
            await new SettingsStore(db).SetQueuePausedAsync(false, CancellationToken.None);
        }

        await using var readDb = CreateDb();
        Assert.False(await new SettingsStore(readDb).GetQueuePausedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Queue_pause_is_operational_state_and_is_not_exported_or_imported()
    {
        await using var db = CreateDb();
        var settings = new SettingsStore(db);
        await settings.SetQueuePausedAsync(true, CancellationToken.None);

        var exported = await settings.ExportSettingsAsync(CancellationToken.None);
        var imported = await settings.ImportSettingsAsync(
            new Dictionary<string, string> { [SettingKeys.QueuePaused] = bool.FalseString },
            CancellationToken.None);

        Assert.DoesNotContain(SettingKeys.QueuePaused, exported.Keys);
        Assert.Equal(0, imported);
        Assert.True(await settings.GetQueuePausedAsync(CancellationToken.None));
    }

    public void Dispose() => _connection.Dispose();
}
