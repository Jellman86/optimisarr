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
        await migrator.MigrateAsync("20260713212602_AddKeepAudioLanguages");
        // Seed at the historical schema boundary without asking the current EF model to insert
        // columns that did not exist in that build.
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO Libraries
                (Name, Path, MediaType, RuleProfile, Enabled, CreatedAt, UpdatedAt, TargetImageFormat)
            VALUES
                ('Photos', '/data/photos', 'Photo', 'ConservativeHevc', 1,
                 '2026-01-01T00:00:00+00:00', '2026-01-01T00:00:00+00:00', 'AVIF');
            """);

        await migrator.MigrateAsync();
        db.ChangeTracker.Clear();

        Assert.Equal("webp", (await db.Libraries.SingleAsync()).TargetImageFormat);
    }

    [Fact]
    public async Task Existing_probed_videos_with_audio_are_queued_for_language_reprobe()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        var options = new DbContextOptionsBuilder<OptimisarrDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;

        await using var db = new OptimisarrDbContext(options);
        var migrator = db.Database.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
        await migrator.MigrateAsync("20260713210047_TrackVideoProfile");
        // Recreate the real upgrade boundary: these rows were probed by a build whose schema had
        // no AudioLanguages column. Raw SQL keeps this test independent of later model additions.
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO Libraries
                (Name, Path, MediaType, RuleProfile, Enabled, CreatedAt, UpdatedAt)
            VALUES
                ('Mixed', '/data/mixed', 'Other', 'ConservativeHevc', 1,
                 '2026-01-01T00:00:00+00:00', '2026-01-01T00:00:00+00:00');

            INSERT INTO MediaFiles
                (LibraryId, Path, RelativePath, SizeBytes, ModifiedAt, DiscoveredAt, UpdatedAt,
                 Status, MediaKind, AudioTrackCount)
            VALUES
                ((SELECT Id FROM Libraries WHERE Path = '/data/mixed'),
                 '/data/mixed/movie.mkv', 'movie.mkv', 1,
                 '2026-01-01T00:00:00+00:00', '2026-01-01T00:00:00+00:00',
                 '2026-01-01T00:00:00+00:00', 'Probed', 'Video', 2),
                ((SELECT Id FROM Libraries WHERE Path = '/data/mixed'),
                 '/data/mixed/song.flac', 'song.flac', 1,
                 '2026-01-01T00:00:00+00:00', '2026-01-01T00:00:00+00:00',
                 '2026-01-01T00:00:00+00:00', 'Probed', 'Audio', 1);
            """);

        await migrator.MigrateAsync("20260713212602_AddKeepAudioLanguages");
        db.ChangeTracker.Clear();

        var files = await db.MediaFiles.OrderBy(file => file.RelativePath).ToListAsync();
        Assert.Equal(MediaFileStatus.Discovered, files.Single(file => file.MediaKind == Optimisarr.Core.Domain.MediaKind.Video).Status);
        Assert.Equal(MediaFileStatus.Probed, files.Single(file => file.MediaKind == Optimisarr.Core.Domain.MediaKind.Audio).Status);
        Assert.All(files, file => Assert.Null(file.AudioLanguages));
    }

    public void Dispose()
    {
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }
}
