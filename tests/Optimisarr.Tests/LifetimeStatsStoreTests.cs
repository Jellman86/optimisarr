using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Stats;
using Optimisarr.Data;

namespace Optimisarr.Tests;

public sealed class LifetimeStatsStoreTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<OptimisarrDbContext> _options;

    public LifetimeStatsStoreTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<OptimisarrDbContext>().UseSqlite(_connection).Options;
        using var db = new OptimisarrDbContext(_options);
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task An_unseeded_tally_reads_as_zero()
    {
        await using var db = CreateDb();
        var stats = await new LifetimeStatsStore(db).GetAsync(CancellationToken.None);

        Assert.Equal(LifetimeStats.Empty, stats);
        Assert.Equal(0, stats.BytesSaved);
        Assert.Equal(0.0, stats.AverageSavingPercent);
    }

    [Fact]
    public async Task Replacements_accrue_and_a_rollback_reverses_its_contribution()
    {
        await using (var db = CreateDb())
        {
            var store = new LifetimeStatsStore(db);
            await store.ApplyReplacementAsync(1000, 400, CancellationToken.None);
            await store.ApplyReplacementAsync(2000, 600, CancellationToken.None);
            await db.SaveChangesAsync();         // the caller's transaction persists the enlisted change
            await store.ApplyRollbackAsync(2000, 600, CancellationToken.None);
            await db.SaveChangesAsync();
        }

        await using var readDb = CreateDb();
        var stats = await new LifetimeStatsStore(readDb).GetAsync(CancellationToken.None);

        Assert.Equal(1, stats.FilesOptimised);
        Assert.Equal(1000, stats.OriginalBytes);
        Assert.Equal(400, stats.OptimisedBytes);
        Assert.Equal(600, stats.BytesSaved);
    }

    [Fact]
    public async Task A_rollback_can_never_drive_a_counter_below_zero()
    {
        await using var db = CreateDb();
        var store = new LifetimeStatsStore(db);

        // Reverse a contribution that was never recorded (e.g. the tally was reset while a
        // replacement was still live): the figures clamp at zero rather than going negative.
        await store.ApplyRollbackAsync(5000, 100, CancellationToken.None);
        await db.SaveChangesAsync();

        var stats = await store.GetAsync(CancellationToken.None);
        Assert.Equal(0, stats.FilesOptimised);
        Assert.Equal(0, stats.OriginalBytes);
        Assert.Equal(0, stats.OptimisedBytes);
    }

    [Fact]
    public async Task Clear_resets_the_tally_to_zero()
    {
        await using var db = CreateDb();
        var store = new LifetimeStatsStore(db);
        await store.ApplyReplacementAsync(9000, 1000, CancellationToken.None);
        await db.SaveChangesAsync();

        await store.ClearAsync(CancellationToken.None);

        var stats = await store.GetAsync(CancellationToken.None);
        Assert.Equal(LifetimeStats.Empty, stats);
    }

    private OptimisarrDbContext CreateDb() => new(_options);

    public void Dispose() => _connection.Dispose();
}
