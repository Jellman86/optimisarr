using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Optimisarr.Data;

namespace Optimisarr.Tests;

public sealed class MigrationTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(),
        "optimisarr-tests",
        $"{Guid.NewGuid():N}.db");

    [Fact]
    public async Task Migrations_apply_to_an_empty_sqlite_database()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        var options = new DbContextOptionsBuilder<OptimisarrDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;

        await using var db = new OptimisarrDbContext(options);
        await db.Database.MigrateAsync();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());

        db.AppSettings.Add(new AppSetting { Key = "migration.smoke", Value = "ok" });
        await db.SaveChangesAsync();
        Assert.Equal("ok", (await db.AppSettings.SingleAsync()).Value);
    }

    [Fact]
    public async Task Avif_library_overrides_migrate_to_the_proven_webp_target()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        var options = new DbContextOptionsBuilder<OptimisarrDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;

        await using var db = new OptimisarrDbContext(options);
        var migrator = db.Database.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
        await migrator.MigrateAsync("20260713210047_TrackVideoProfile");

        db.Libraries.Add(new Library
        {
            Name = "Photos",
            Path = "/data/photos",
            TargetImageFormat = "AVIF"
        });
        await db.SaveChangesAsync();

        await migrator.MigrateAsync();
        db.ChangeTracker.Clear();

        Assert.Equal("webp", (await db.Libraries.SingleAsync()).TargetImageFormat);
    }

    public void Dispose()
    {
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }
}
