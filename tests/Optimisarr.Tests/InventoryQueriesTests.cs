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

        var all = await inventory.QueryAsync(null, InventoryFilter.All, null, 1, 0, InventorySort.Default, interlacedOnly: false, default);
        Assert.Equal(3, all.Total);
        Assert.Equal(new InventoryCounts(All: 3, Eligible: 1, Skipped: 1, Unprobed: 1, Interlaced: 0), all.Counts);

        var eligible = await inventory.QueryAsync(null, InventoryFilter.Eligible, null, 1, 0, InventorySort.Default, interlacedOnly: false, default);
        var only = Assert.Single(eligible.Items);
        Assert.Equal("a-eligible.mkv", only.File.RelativePath);
        Assert.True(only.Eligible);

        var unprobed = await inventory.QueryAsync(null, InventoryFilter.Unprobed, null, 1, 0, InventorySort.Default, interlacedOnly: false, default);
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

        var page = await inventory.QueryAsync(null, InventoryFilter.Eligible, null, page: 1, pageSize: 5, InventorySort.Default, interlacedOnly: false, default);

        Assert.Equal(7, page.Total);                       // filtered total, not the page size
        Assert.Equal(5, page.Items.Count);
        Assert.Equal("01.mkv", page.Items[0].File.RelativePath);   // ordered by path
        Assert.Equal(7, page.Counts.Eligible);
    }

    [Theory]
    [InlineData(InventorySort.NameAscending, new[] { "a-eligible.mkv", "B-skipped.mkv", "c-unprobed.mkv", "d-eligible.mkv" })]
    [InlineData(InventorySort.NameDescending, new[] { "d-eligible.mkv", "c-unprobed.mkv", "B-skipped.mkv", "a-eligible.mkv" })]
    [InlineData(InventorySort.SizeAscending, new[] { "B-skipped.mkv", "c-unprobed.mkv", "a-eligible.mkv", "d-eligible.mkv" })]
    [InlineData(InventorySort.SizeDescending, new[] { "d-eligible.mkv", "a-eligible.mkv", "c-unprobed.mkv", "B-skipped.mkv" })]
    [InlineData(InventorySort.VerdictAscending, new[] { "a-eligible.mkv", "d-eligible.mkv", "c-unprobed.mkv", "B-skipped.mkv" })]
    [InlineData(InventorySort.VerdictDescending, new[] { "B-skipped.mkv", "c-unprobed.mkv", "a-eligible.mkv", "d-eligible.mkv" })]
    public async Task QueryAsync_orders_rows_by_the_requested_sort_with_path_tie_breaks(InventorySort sort, string[] expected)
    {
        await SeedSortableLibraryAsync();
        await using var readDb = new OptimisarrDbContext(_options);
        var inventory = new InventoryQueries(readDb, new CandidateService(readDb));

        var page = await inventory.QueryAsync(null, InventoryFilter.All, null, 1, 0, sort, interlacedOnly: false, default);

        Assert.Equal(expected, page.Items.Select(row => row.File.RelativePath));
    }

    [Fact]
    public async Task QueryAsync_without_a_sort_keeps_the_path_order()
    {
        await SeedSortableLibraryAsync();
        await using var readDb = new OptimisarrDbContext(_options);
        var inventory = new InventoryQueries(readDb, new CandidateService(readDb));

        var page = await inventory.QueryAsync(null, InventoryFilter.All, null, 1, 0, InventorySort.Default, interlacedOnly: false, default);

        // SQLite's default collation is binary, so the unsorted path order puts "B" before "a".
        Assert.Equal(["B-skipped.mkv", "a-eligible.mkv", "c-unprobed.mkv", "d-eligible.mkv"],
            page.Items.Select(row => row.File.RelativePath));
    }

    [Fact]
    public async Task QueryAsync_sorts_before_paging_so_pages_do_not_overlap()
    {
        await SeedSortableLibraryAsync();
        await using var readDb = new OptimisarrDbContext(_options);
        var inventory = new InventoryQueries(readDb, new CandidateService(readDb));

        var first = await inventory.QueryAsync(null, InventoryFilter.All, null, 1, 2, InventorySort.SizeDescending, interlacedOnly: false, default);
        var second = await inventory.QueryAsync(null, InventoryFilter.All, null, 2, 2, InventorySort.SizeDescending, interlacedOnly: false, default);

        Assert.Equal(["d-eligible.mkv", "a-eligible.mkv"], first.Items.Select(row => row.File.RelativePath));
        Assert.Equal(["c-unprobed.mkv", "B-skipped.mkv"], second.Items.Select(row => row.File.RelativePath));
        Assert.Equal(4, first.Total);
    }

    [Fact]
    public async Task QueryAsync_applies_the_sort_after_the_filter_and_search()
    {
        await SeedSortableLibraryAsync();
        await using var readDb = new OptimisarrDbContext(_options);
        var inventory = new InventoryQueries(readDb, new CandidateService(readDb));

        var eligible = await inventory.QueryAsync(null, InventoryFilter.Eligible, null, 1, 0, InventorySort.SizeDescending, interlacedOnly: false, default);
        var searched = await inventory.QueryAsync(null, InventoryFilter.All, "eligible", 1, 0, InventorySort.NameDescending, interlacedOnly: false, default);

        Assert.Equal(["d-eligible.mkv", "a-eligible.mkv"], eligible.Items.Select(row => row.File.RelativePath));
        Assert.Equal(["d-eligible.mkv", "a-eligible.mkv"], searched.Items.Select(row => row.File.RelativePath));
        Assert.Equal(new InventoryCounts(All: 2, Eligible: 2, Skipped: 0, Unprobed: 0, Interlaced: 0), searched.Counts);
    }

    [Fact]
    public async Task QueryAsync_name_sort_orders_by_filename_across_subdirectories_and_separators()
    {
        await using (var db = new OptimisarrDbContext(_options))
        {
            var library = new Library { Name = "Mixed", Path = "/data/mixed", RuleProfile = RuleProfile.ConservativeHevc };
            db.Libraries.Add(library);
            await db.SaveChangesAsync();
            db.MediaFiles.Add(Probed(library.Id, "Films/Orbit/Orbit.mkv", "h264"));
            db.MediaFiles.Add(Probed(library.Id, "Music\\Album\\01 - Track.flac", "h264"));
            db.MediaFiles.Add(Probed(library.Id, "Zed/Arrival.mkv", "h264"));
            db.MediaFiles.Add(Probed(library.Id, "Alpha/Arrival.mkv", "h264"));
            await db.SaveChangesAsync();
        }

        await using var readDb = new OptimisarrDbContext(_options);
        var inventory = new InventoryQueries(readDb, new CandidateService(readDb));

        var ascending = await inventory.QueryAsync(null, InventoryFilter.All, null, 1, 0, InventorySort.NameAscending, interlacedOnly: false, default);
        var descending = await inventory.QueryAsync(null, InventoryFilter.All, null, 1, 0, InventorySort.NameDescending, interlacedOnly: false, default);

        // Filename leads; identical filenames tie-break deterministically by full relative path.
        Assert.Equal(["Music\\Album\\01 - Track.flac", "Alpha/Arrival.mkv", "Zed/Arrival.mkv", "Films/Orbit/Orbit.mkv"],
            ascending.Items.Select(row => row.File.RelativePath));
        Assert.Equal(["Films/Orbit/Orbit.mkv", "Alpha/Arrival.mkv", "Zed/Arrival.mkv", "Music\\Album\\01 - Track.flac"],
            descending.Items.Select(row => row.File.RelativePath));
    }

    [Fact]
    public async Task QueryAsync_breaks_equal_sizes_by_path_then_id()
    {
        await using (var db = new OptimisarrDbContext(_options))
        {
            var library = new Library { Name = "Films", Path = "/data/films", RuleProfile = RuleProfile.ConservativeHevc };
            db.Libraries.Add(library);
            await db.SaveChangesAsync();
            db.MediaFiles.Add(Probed(library.Id, "z.mkv", "h264"));
            db.MediaFiles.Add(Probed(library.Id, "m.mkv", "h264"));
            db.MediaFiles.Add(Probed(library.Id, "A.mkv", "h264"));
            await db.SaveChangesAsync();
        }

        await using var readDb = new OptimisarrDbContext(_options);
        var inventory = new InventoryQueries(readDb, new CandidateService(readDb));

        var ascending = await inventory.QueryAsync(null, InventoryFilter.All, null, 1, 0, InventorySort.SizeAscending, interlacedOnly: false, default);
        var descending = await inventory.QueryAsync(null, InventoryFilter.All, null, 1, 0, InventorySort.SizeDescending, interlacedOnly: false, default);

        // Equal sizes keep a stable, case-insensitive path order in both directions so paging stays deterministic.
        Assert.Equal(["A.mkv", "m.mkv", "z.mkv"], ascending.Items.Select(row => row.File.RelativePath));
        Assert.Equal(["A.mkv", "m.mkv", "z.mkv"], descending.Items.Select(row => row.File.RelativePath));
    }

    [Fact]
    public async Task QueryAsync_lists_only_interlaced_files_when_asked()
    {
        await SeedFieldOrderLibraryAsync();
        await using var readDb = new OptimisarrDbContext(_options);
        var inventory = new InventoryQueries(readDb, new CandidateService(readDb));

        var interlaced = await inventory.QueryAsync(null, InventoryFilter.All, null, 1, 0, InventorySort.Default, interlacedOnly: true, default);

        // Every interlaced field order counts; progressive, unknown, and never-probed files do not.
        Assert.Equal(["bb.mkv", "bt.mkv", "tb.mkv", "tt.mkv"], interlaced.Items.Select(row => row.File.RelativePath));
        Assert.Equal(4, interlaced.Total);
        Assert.Equal(["bb", "bt", "tb", "tt"], interlaced.Items.Select(row => row.File.FieldOrder));
    }

    [Fact]
    public async Task QueryAsync_counts_interlaced_files_in_scope_whether_or_not_the_filter_is_on()
    {
        await SeedFieldOrderLibraryAsync();
        await using var readDb = new OptimisarrDbContext(_options);
        var inventory = new InventoryQueries(readDb, new CandidateService(readDb));

        var everything = await inventory.QueryAsync(null, InventoryFilter.All, null, 1, 0, InventorySort.Default, interlacedOnly: false, default);
        var interlaced = await inventory.QueryAsync(null, InventoryFilter.All, null, 1, 0, InventorySort.Default, interlacedOnly: true, default);

        Assert.Equal(7, everything.Total);
        Assert.Equal(4, everything.Counts.Interlaced);
        // With the filter on, the verdict tallies describe the interlaced files alone.
        Assert.Equal(4, interlaced.Counts.All);
        Assert.Equal(4, interlaced.Counts.Interlaced);
    }

    [Fact]
    public async Task QueryAsync_combines_the_interlaced_filter_with_library_search_verdict_and_paging()
    {
        var films = await SeedFieldOrderLibraryAsync();
        await using (var db = new OptimisarrDbContext(_options))
        {
            // An interlaced file in another library stays out of a one-library view.
            var tv = new Library { Name = "TV", Path = "/data/tv", RuleProfile = RuleProfile.ConservativeHevc };
            db.Libraries.Add(tv);
            await db.SaveChangesAsync();
            db.MediaFiles.Add(Probed(tv.Id, "tt-recording.ts", "mpeg2video", fieldOrder: "tt"));
            await db.SaveChangesAsync();
        }
        await using var readDb = new OptimisarrDbContext(_options);
        var inventory = new InventoryQueries(readDb, new CandidateService(readDb));

        var inFilms = await inventory.QueryAsync(films, InventoryFilter.All, null, 1, 0, InventorySort.Default, interlacedOnly: true, default);
        var searched = await inventory.QueryAsync(null, InventoryFilter.All, "tt", 1, 0, InventorySort.Default, interlacedOnly: true, default);
        var skipped = await inventory.QueryAsync(films, InventoryFilter.Skipped, null, 1, 0, InventorySort.Default, interlacedOnly: true, default);
        var secondPage = await inventory.QueryAsync(films, InventoryFilter.All, null, 2, 3, InventorySort.NameAscending, interlacedOnly: true, default);

        Assert.Equal(4, inFilms.Total);
        Assert.DoesNotContain(inFilms.Items, row => row.File.RelativePath == "tt-recording.ts");
        Assert.Equal(["tt-recording.ts", "tt.mkv"], searched.Items.Select(row => row.File.RelativePath).Order());
        Assert.Equal(2, searched.Counts.Interlaced);
        // The rules skip an interlaced source in a library that does not deinterlace.
        Assert.Equal(4, skipped.Total);
        Assert.All(skipped.Items, row => Assert.False(row.Eligible));
        Assert.Equal(4, secondPage.Total);
        Assert.Equal(["tt.mkv"], secondPage.Items.Select(row => row.File.RelativePath));
    }

    [Theory]
    [InlineData("name_asc", InventorySort.NameAscending)]
    [InlineData("NAME_DESC", InventorySort.NameDescending)]
    [InlineData("size_asc", InventorySort.SizeAscending)]
    [InlineData("Size_Desc", InventorySort.SizeDescending)]
    [InlineData("verdict_asc", InventorySort.VerdictAscending)]
    [InlineData("verdict_desc", InventorySort.VerdictDescending)]
    public void TryParseSort_accepts_each_documented_value_case_insensitively(string value, InventorySort expected)
    {
        Assert.True(InventoryQueries.TryParseSort(value, out var sort));
        Assert.Equal(expected, sort);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void TryParseSort_treats_a_missing_value_as_the_default_order(string? value)
    {
        Assert.True(InventoryQueries.TryParseSort(value, out var sort));
        Assert.Equal(InventorySort.Default, sort);
    }

    [Theory]
    [InlineData("size")]
    [InlineData("SizeDescending")]
    [InlineData("1")]
    [InlineData("default")]
    public void TryParseSort_rejects_unknown_values(string value)
    {
        Assert.False(InventoryQueries.TryParseSort(value, out _));
    }

    // Four files covering every verdict, distinct sizes, and a capitalised name so the
    // case-insensitive path tie-break is observable against SQLite's binary default order.
    private async Task SeedSortableLibraryAsync()
    {
        await using var db = new OptimisarrDbContext(_options);
        var library = new Library { Name = "Films", Path = "/data/films", RuleProfile = RuleProfile.ConservativeHevc };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();
        db.MediaFiles.Add(Probed(library.Id, "a-eligible.mkv", "h264", sizeBytes: 3_000_000_000));
        db.MediaFiles.Add(Probed(library.Id, "B-skipped.mkv", "hevc", sizeBytes: 1_000_000_000));
        db.MediaFiles.Add(new MediaFile { LibraryId = library.Id, Path = "/data/films/c-unprobed.mkv", RelativePath = "c-unprobed.mkv", SizeBytes = 2_000_000_000, Status = MediaFileStatus.Discovered });
        db.MediaFiles.Add(Probed(library.Id, "d-eligible.mkv", "h264", sizeBytes: 9_000_000_000));
        await db.SaveChangesAsync();
    }

    // One file per field order ffprobe reports, plus a never-probed file, all re-encode candidates by codec.
    private async Task<int> SeedFieldOrderLibraryAsync()
    {
        await using var db = new OptimisarrDbContext(_options);
        var library = new Library { Name = "Films", Path = "/data/films", RuleProfile = RuleProfile.ConservativeHevc };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();
        foreach (var fieldOrder in new[] { "tt", "bb", "tb", "bt", "progressive", "unknown" })
        {
            db.MediaFiles.Add(Probed(library.Id, $"{fieldOrder}.mkv", "mpeg2video", fieldOrder: fieldOrder));
        }
        db.MediaFiles.Add(Probed(library.Id, "not-yet-read.mkv", "mpeg2video"));
        await db.SaveChangesAsync();
        return library.Id;
    }

    private static MediaFile Probed(int libraryId, string relativePath, string videoCodec, long sizeBytes = 5_000_000_000, string? fieldOrder = null) => new()
    {
        LibraryId = libraryId,
        Path = $"/data/films/{relativePath}",
        RelativePath = relativePath,
        SizeBytes = sizeBytes,
        Status = MediaFileStatus.Probed,
        Container = "matroska,webm",
        VideoCodec = videoCodec,
        Width = 1920,
        Height = 1080,
        FieldOrder = fieldOrder,
        ProbedAt = DateTimeOffset.UtcNow
    };

    public void Dispose() => _connection.Dispose();
}
