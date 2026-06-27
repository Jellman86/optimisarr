using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Library;
using Optimisarr.Data;

namespace Optimisarr.Tests;

public sealed class MediaQueriesTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<OptimisarrDbContext> _options;

    public MediaQueriesTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:;Foreign Keys=True");
        _connection.Open();
        _options = new DbContextOptionsBuilder<OptimisarrDbContext>().UseSqlite(_connection).Options;
        using var db = new OptimisarrDbContext(_options);
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task QueryAsync_orders_by_path_and_returns_everything_by_default()
    {
        int libraryId = await SeedAsync(
            ("b.mkv", MediaFileStatus.Probed),
            ("a.mkv", MediaFileStatus.Probed),
            ("c.mkv", MediaFileStatus.Discovered));

        await using var db = new OptimisarrDbContext(_options);
        var result = await MediaQueries.QueryAsync(db, new MediaQuery { LibraryId = libraryId }, default);

        Assert.Equal(3, result.Total);
        Assert.Equal(new[] { "a.mkv", "b.mkv", "c.mkv" }, result.Items.Select(item => item.RelativePath));
    }

    [Fact]
    public async Task QueryAsync_filters_by_status()
    {
        int libraryId = await SeedAsync(
            ("a.mkv", MediaFileStatus.Probed),
            ("b.mkv", MediaFileStatus.ProbeFailed),
            ("c.mkv", MediaFileStatus.Probed));

        await using var db = new OptimisarrDbContext(_options);
        var result = await MediaQueries.QueryAsync(
            db, new MediaQuery { LibraryId = libraryId, Status = MediaFileStatus.Probed }, default);

        Assert.Equal(2, result.Total);
        Assert.All(result.Items, item => Assert.Equal("Probed", item.Status));
    }

    [Fact]
    public async Task QueryAsync_searches_the_path_case_insensitively()
    {
        int libraryId = await SeedAsync(
            ("Movies/Inception.mkv", MediaFileStatus.Probed),
            ("Movies/Tenet.mkv", MediaFileStatus.Probed),
            ("Shows/Severance.mkv", MediaFileStatus.Probed));

        await using var db = new OptimisarrDbContext(_options);
        var result = await MediaQueries.QueryAsync(
            db, new MediaQuery { LibraryId = libraryId, Search = "movies" }, default);

        Assert.Equal(2, result.Total);
        Assert.All(result.Items, item => Assert.StartsWith("Movies/", item.RelativePath));
    }

    [Fact]
    public async Task QueryAsync_pages_while_reporting_the_full_total()
    {
        int libraryId = await SeedAsync(
            Enumerable.Range(1, 25).Select(n => ($"{n:D2}.mkv", MediaFileStatus.Probed)).ToArray());

        await using var db = new OptimisarrDbContext(_options);
        var page1 = await MediaQueries.QueryAsync(
            db, new MediaQuery { LibraryId = libraryId, Page = 1, PageSize = 10 }, default);
        var page3 = await MediaQueries.QueryAsync(
            db, new MediaQuery { LibraryId = libraryId, Page = 3, PageSize = 10 }, default);

        Assert.Equal(25, page1.Total);
        Assert.Equal(10, page1.Items.Count);
        Assert.Equal("01.mkv", page1.Items[0].RelativePath);   // ordered by path
        Assert.Equal(5, page3.Items.Count);                    // last page is partial
        Assert.Equal(25, page3.Total);
    }

    private async Task<int> SeedAsync(params (string Path, MediaFileStatus Status)[] files)
    {
        await using var db = new OptimisarrDbContext(_options);
        var library = new Library { Name = "Films", Path = "/data/films" };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();

        foreach (var (path, status) in files)
        {
            db.MediaFiles.Add(new MediaFile
            {
                LibraryId = library.Id,
                Path = $"/data/films/{path}",
                RelativePath = path,
                SizeBytes = 1_000_000,
                Status = status
            });
        }
        await db.SaveChangesAsync();
        return library.Id;
    }

    public void Dispose() => _connection.Dispose();
}
