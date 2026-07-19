using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Library;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Library;
using Optimisarr.Data;

namespace Optimisarr.Tests;

public sealed class LibraryInventoryServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<OptimisarrDbContext> _options;
    private readonly string _root = Directory.CreateTempSubdirectory("optimisarr-inv-").FullName;

    public LibraryInventoryServiceTests()
    {
        // A shared in-memory SQLite database that lives as long as the connection.
        // Foreign Keys=True enforces the cascade delete from Library to MediaFile.
        _connection = new SqliteConnection("DataSource=:memory:;Foreign Keys=True");
        _connection.Open();
        _options = new DbContextOptionsBuilder<OptimisarrDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = new OptimisarrDbContext(_options);
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task Scan_associates_discovered_files_with_their_library_and_is_idempotent()
    {
        WriteMediaFile("Show/S01E01.mkv");
        WriteMediaFile("Show/S01E02.mkv");

        var library = await CreateLibraryAsync();

        var first = await ScanAsync(library);
        Assert.Equal(2, first.Discovered);
        Assert.Equal(2, first.Added);
        Assert.Equal(0, first.Updated);

        await using (var db = new OptimisarrDbContext(_options))
        {
            var files = await db.MediaFiles.ToListAsync();
            Assert.Equal(2, files.Count);
            Assert.All(files, file => Assert.Equal((int?)library.Id, file.LibraryId));
        }

        // Re-scanning an unchanged library must not add or update anything.
        var second = await ScanAsync(library);
        Assert.Equal(2, second.Discovered);
        Assert.Equal(0, second.Added);
        Assert.Equal(0, second.Updated);
    }

    [Fact]
    public async Task Deleting_a_library_cascades_to_its_media_files()
    {
        WriteMediaFile("Movie.mkv");
        var library = await CreateLibraryAsync();
        await ScanAsync(library);

        await using (var db = new OptimisarrDbContext(_options))
        {
            db.Libraries.Remove(await db.Libraries.SingleAsync());
            await db.SaveChangesAsync();
        }

        await using (var db = new OptimisarrDbContext(_options))
        {
            Assert.Empty(db.MediaFiles);
        }
    }

    [Fact]
    public async Task Scan_removes_a_row_whose_file_has_vanished_and_cascades_its_jobs()
    {
        WriteMediaFile("Show/S01E01 WEBDL-1080p.mkv");
        WriteMediaFile("Show/S01E02.mkv");
        var library = await CreateLibraryAsync();
        await ScanAsync(library);

        // A queued/failed job exists for the file (as job 3328 did for the renamed Godzilla file).
        int vanishedId;
        await using (var db = new OptimisarrDbContext(_options))
        {
            var media = await db.MediaFiles.SingleAsync(f => f.Path.Contains("S01E01"));
            vanishedId = media.Id;
            db.Jobs.Add(new Job { MediaFileId = media.Id, Status = JobStatus.Failed });
            await db.SaveChangesAsync();
        }

        // Radarr/Sonarr upgraded the release and renamed the file, so the old path is now dangling.
        File.Delete(Path.Combine(_root, "Show", "S01E01 WEBDL-1080p.mkv"));

        var summary = await ScanAsync(library);

        Assert.Equal(1, summary.Removed);
        await using (var db = new OptimisarrDbContext(_options))
        {
            Assert.Null(await db.MediaFiles.FindAsync(vanishedId));   // stale row gone
            Assert.Empty(db.Jobs);                                    // its job cascaded away
            Assert.Single(db.MediaFiles);                             // the surviving file remains
        }
    }

    [Fact]
    public async Task Scan_keeps_a_vanished_row_that_has_replacement_history()
    {
        WriteMediaFile("Movie.mkv");
        var library = await CreateLibraryAsync();
        await ScanAsync(library);

        int mediaId;
        await using (var db = new OptimisarrDbContext(_options))
        {
            var media = await db.MediaFiles.SingleAsync();
            mediaId = media.Id;
            var job = new Job { MediaFileId = mediaId, Status = JobStatus.Completed };
            db.Jobs.Add(job);
            await db.SaveChangesAsync();
            db.Replacements.Add(new Replacement
            {
                JobId = job.Id,
                MediaFileId = mediaId,
                OriginalPath = media.Path,
                QuarantinePath = "/trash/movie.mkv",
                FinalPath = media.Path,
                OriginalSizeBytes = 10,
                NewSizeBytes = 5,
                Status = ReplacementStatus.Replaced
            });
            await db.SaveChangesAsync();
        }

        File.Delete(Path.Combine(_root, "Movie.mkv"));

        var summary = await ScanAsync(library);

        // The file is gone, but its rollback history must survive — so the row is kept, not pruned.
        Assert.Equal(0, summary.Removed);
        await using (var db = new OptimisarrDbContext(_options))
        {
            Assert.NotNull(await db.MediaFiles.FindAsync(mediaId));
            Assert.Single(db.Replacements);
        }
    }

    [Fact]
    public async Task Scan_clears_stale_track_languages_when_a_file_changes()
    {
        WriteMediaFile("Movie.mkv");
        var library = await CreateLibraryAsync();
        await ScanAsync(library);

        await using (var db = new OptimisarrDbContext(_options))
        {
            var media = await db.MediaFiles.SingleAsync();
            media.AudioLanguages = "eng, fra";
            media.SubtitleLanguages = "eng, spa";
            await db.SaveChangesAsync();
        }

        // The file changed on disk, so every probe result — including the track
        // languages the removal rules rely on — is stale and must be re-read.
        var fullPath = Path.Combine(_root, "Movie.mkv");
        File.WriteAllText(fullPath, "different content");
        File.SetLastWriteTimeUtc(fullPath, DateTime.UtcNow.AddMinutes(-30));

        await ScanAsync(library);

        await using (var db = new OptimisarrDbContext(_options))
        {
            var media = await db.MediaFiles.SingleAsync();
            Assert.Null(media.AudioLanguages);
            Assert.Null(media.SubtitleLanguages);
        }
    }

    private async Task<Library> CreateLibraryAsync()
    {
        await using var db = new OptimisarrDbContext(_options);
        var library = new Library { Name = "Test", Path = _root, MediaType = MediaType.Tv };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();
        return library;
    }

    private async Task<ScanSummary> ScanAsync(Library library)
    {
        await using var db = new OptimisarrDbContext(_options);
        var service = new LibraryInventoryService(db, new LibraryScanner(), new MediaProbeService(), new ImageMarkerService());
        return await service.ScanAsync(library, CancellationToken.None);
    }

    private void WriteMediaFile(string relativePath)
    {
        var fullPath = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, "content");
        // Make it settled (older than the default settling period).
        File.SetLastWriteTimeUtc(fullPath, DateTime.UtcNow.AddHours(-1));
    }

    public void Dispose()
    {
        _connection.Dispose();
        Directory.Delete(_root, recursive: true);
    }
}
