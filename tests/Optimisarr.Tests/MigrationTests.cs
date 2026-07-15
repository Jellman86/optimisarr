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

    [Fact]
    public async Task Legacy_global_vmaf_policy_is_materialised_per_library_and_removed()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        var options = new DbContextOptionsBuilder<OptimisarrDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;

        await using var db = new OptimisarrDbContext(options);
        var migrator = db.Database.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
        await migrator.MigrateAsync("20260715174521_AddLibraryVmafPolicy");
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO Libraries
                (Name, Path, MediaType, RuleProfile, Enabled, CreatedAt, UpdatedAt)
            VALUES
                ('Films', '/data/films', 'Film', 'ConservativeHevc', 1,
                 '2026-01-01T00:00:00+00:00', '2026-01-01T00:00:00+00:00');

            INSERT INTO Libraries
                (Name, Path, MediaType, RuleProfile, Enabled, CreatedAt, UpdatedAt,
                 VmafQualityGateEnabled, MinVmafHarmonicMean, MinVmafMin,
                 MinVmafCatastrophicMin, ClipVmafEnabled, VmafFrameSubsample)
            VALUES
                ('Archive', '/data/archive', 'Film', 'ConservativeHevc', 1,
                 '2026-01-01T00:00:00+00:00', '2026-01-01T00:00:00+00:00',
                 0, 96, 90, 70, 0, 1);

            INSERT INTO AppSettings ("Key", "Value", UpdatedAt) VALUES
                ('verification.qualityGateEnabled', 'True', '2026-01-01T00:00:00+00:00'),
                ('verification.minimumVmafHarmonicMean', '88', '2026-01-01T00:00:00+00:00'),
                ('verification.minimumVmafMin', '72', '2026-01-01T00:00:00+00:00'),
                ('verification.minimumVmafCatastrophicMin', '42', '2026-01-01T00:00:00+00:00'),
                ('verification.clipVmafEnabled', 'True', '2026-01-01T00:00:00+00:00'),
                ('verification.vmafFrameSubsample', '3', '2026-01-01T00:00:00+00:00');
            """);

        await migrator.MigrateAsync();
        db.ChangeTracker.Clear();

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
        Assert.DoesNotContain(
            await db.AppSettings.Select(setting => setting.Key).ToListAsync(),
            key => key.StartsWith("verification.minimumVmaf", StringComparison.Ordinal)
                || key is "verification.qualityGateEnabled"
                    or "verification.clipVmafEnabled"
                    or "verification.vmafFrameSubsample");
    }

    public void Dispose()
    {
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }
}
