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

        // Insert with raw SQL, not the entity: the schema is checkpointed at an old
        // migration, while an entity insert writes every column of the *current* model —
        // any column added by a later migration would make this fixture fail to insert.
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "Libraries"
                ("Name", "Path", "MediaType", "RuleProfile", "Enabled", "Priority",
                 "SkipEfficientSources", "OptimiseDolbyVision", "DownmixToStereo",
                 "ReencodeLossyAudio", "TargetImageFormat", "ReencodeLossyImages",
                 "ImageDownscaleMode", "ImageDownscaleValue", "MoveOnComplete", "MoveOverwrite",
                 "AutoEnqueueEnabled", "AutoEnqueueWindowStart", "AutoEnqueueWindowEnd",
                 "AutoReplace", "CreatedAt", "UpdatedAt")
            VALUES
                ('Photos', '/data/photos', 'Other', 'ConservativeHevc', 1, 0,
                 1, 0, 0,
                 0, 'AVIF', 0,
                 'None', 0, 0, 0,
                 0, '00:00:00', '00:00:00',
                 0, '2026-07-13 00:00:00+00:00', '2026-07-13 00:00:00+00:00')
            """);

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
