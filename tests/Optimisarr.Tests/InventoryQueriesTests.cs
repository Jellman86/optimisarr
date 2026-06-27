using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Library;
using Optimisarr.Core.Domain;
using Optimisarr.Data;

namespace Optimisarr.Tests;

public sealed class InventoryQueriesTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<OptimisarrDbContext> _options;

    public InventoryQueriesTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:;Foreign Keys=True");
        _connection.Open();
        _options = new DbContextOptionsBuilder<OptimisarrDbContext>().UseSqlite(_connection).Options;
        using var db = new OptimisarrDbContext(_options);
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task QueryAsync_merges_verdicts_counts_buckets_and_filters()
    {
        await using (var db = new OptimisarrDbContext(_options))
        {
            var library = new Library { Name = "Films", Path = "/data/films", RuleProfile = RuleProfile.ConservativeHevc };
            db.Libraries.Add(library);
            await db.SaveChangesAsync();

            // Eligible (h264 → hevc), skipped (already hevc), and unprobed (no verdict).
            db.MediaFiles.Add(Probed(library.Id, "a-eligible.mkv", "h264"));
            db.MediaFiles.Add(Probed(library.Id, "b-skipped.mkv", "hevc"));
            db.MediaFiles.Add(new MediaFile { LibraryId = library.Id, Path = "/data/films/c-unprobed.mkv", RelativePath = "c-unprobed.mkv", SizeBytes = 5_000_000_000, Status = MediaFileStatus.Discovered });
            await db.SaveChangesAsync();
        }

        await using var readDb = new OptimisarrDbContext(_options);
        var inventory = new InventoryQueries(readDb, new CandidateService(readDb));

        var all = await inventory.QueryAsync(null, InventoryFilter.All, null, 1, 0, default);
        Assert.Equal(3, all.Total);
        Assert.Equal(new InventoryCounts(All: 3, Eligible: 1, Skipped: 1, Unprobed: 1), all.Counts);

        var eligible = await inventory.QueryAsync(null, InventoryFilter.Eligible, null, 1, 0, default);
        var only = Assert.Single(eligible.Items);
        Assert.Equal("a-eligible.mkv", only.File.RelativePath);
        Assert.True(only.Eligible);

        var unprobed = await inventory.QueryAsync(null, InventoryFilter.Unprobed, null, 1, 0, default);
        Assert.Null(Assert.Single(unprobed.Items).Eligible);
    }

    [Fact]
    public async Task QueryAsync_pages_the_filtered_set_while_keeping_full_counts()
    {
        await using (var db = new OptimisarrDbContext(_options))
        {
            var library = new Library { Name = "Films", Path = "/data/films", RuleProfile = RuleProfile.ConservativeHevc };
            db.Libraries.Add(library);
            await db.SaveChangesAsync();
            for (var n = 1; n <= 7; n++)
            {
                db.MediaFiles.Add(Probed(library.Id, $"{n:D2}.mkv", "h264"));   // all eligible
            }
            await db.SaveChangesAsync();
        }

        await using var readDb = new OptimisarrDbContext(_options);
        var inventory = new InventoryQueries(readDb, new CandidateService(readDb));

        var page = await inventory.QueryAsync(null, InventoryFilter.Eligible, null, page: 1, pageSize: 5, default);

        Assert.Equal(7, page.Total);                       // filtered total, not the page size
        Assert.Equal(5, page.Items.Count);
        Assert.Equal("01.mkv", page.Items[0].File.RelativePath);   // ordered by path
        Assert.Equal(7, page.Counts.Eligible);
    }

    private static MediaFile Probed(int libraryId, string relativePath, string videoCodec) => new()
    {
        LibraryId = libraryId,
        Path = $"/data/films/{relativePath}",
        RelativePath = relativePath,
        SizeBytes = 5_000_000_000,
        Status = MediaFileStatus.Probed,
        Container = "matroska,webm",
        VideoCodec = videoCodec,
        Width = 1920,
        Height = 1080,
        ProbedAt = DateTimeOffset.UtcNow
    };

    public void Dispose() => _connection.Dispose();
}
