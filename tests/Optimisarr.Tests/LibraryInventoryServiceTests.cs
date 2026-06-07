using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Library;
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
        var service = new LibraryInventoryService(db, new LibraryScanner(), new MediaProbeService());
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
