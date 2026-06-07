using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Library;
using Optimisarr.Core.Domain;
using Optimisarr.Data;

namespace Optimisarr.Tests;

public sealed class CandidateServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<OptimisarrDbContext> _options;

    public CandidateServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:;Foreign Keys=True");
        _connection.Open();
        _options = new DbContextOptionsBuilder<OptimisarrDbContext>().UseSqlite(_connection).Options;

        using var db = new OptimisarrDbContext(_options);
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task Evaluates_probed_files_against_their_library_profile()
    {
        await using (var db = new OptimisarrDbContext(_options))
        {
            var library = new Library { Name = "Films", Path = "/data/films", RuleProfile = RuleProfile.ConservativeHevc };
            db.Libraries.Add(library);
            await db.SaveChangesAsync();

            db.MediaFiles.Add(Probed(library.Id, "a.mkv", videoCodec: "h264"));   // eligible: h264 -> hevc
            db.MediaFiles.Add(Probed(library.Id, "b.mkv", videoCodec: "hevc"));   // skipped: already hevc
            db.MediaFiles.Add(Probed(library.Id, "c.mkv", videoCodec: "h264", isHdr: true)); // skipped: HDR excluded
            await db.SaveChangesAsync();
        }

        var results = await EvaluateAsync(libraryId: null);

        Assert.Equal(3, results.Count);
        Assert.True(Single(results, "a.mkv").Eligible);
        Assert.False(Single(results, "b.mkv").Eligible);
        Assert.Contains("Already", Single(results, "b.mkv").Reason);
        Assert.False(Single(results, "c.mkv").Eligible);
        Assert.Contains("HDR", Single(results, "c.mkv").Reason);
    }

    [Fact]
    public async Task Applies_per_library_overrides_when_evaluating()
    {
        await using (var db = new OptimisarrDbContext(_options))
        {
            var library = new Library
            {
                Name = "Films",
                Path = "/data/films",
                RuleProfile = RuleProfile.ConservativeHevc,
                HdrHandling = HdrHandling.TonemapToSdr,   // HDR becomes eligible
                ExcludePaths = "Extras"                    // anything under Extras is skipped
            };
            db.Libraries.Add(library);
            await db.SaveChangesAsync();

            db.MediaFiles.Add(Probed(library.Id, "feature.mkv", videoCodec: "h264", isHdr: true));
            db.MediaFiles.Add(Probed(library.Id, "Extras/clip.mkv", videoCodec: "h264"));
            await db.SaveChangesAsync();
        }

        var results = await EvaluateAsync(libraryId: null);

        Assert.True(Single(results, "feature.mkv").Eligible);          // tonemap override allows HDR
        Assert.False(Single(results, "Extras/clip.mkv").Eligible);     // path override skips it
        Assert.Contains("Extras", Single(results, "Extras/clip.mkv").Reason);
    }

    [Fact]
    public async Task Ignores_files_that_have_not_been_probed()
    {
        await using (var db = new OptimisarrDbContext(_options))
        {
            var library = new Library { Name = "Films", Path = "/data/films" };
            db.Libraries.Add(library);
            await db.SaveChangesAsync();

            db.MediaFiles.Add(new MediaFile
            {
                LibraryId = library.Id,
                Path = "/data/films/unprobed.mkv",
                RelativePath = "unprobed.mkv",
                SizeBytes = 5_000_000_000,
                Status = MediaFileStatus.Discovered
            });
            await db.SaveChangesAsync();
        }

        var results = await EvaluateAsync(libraryId: null);

        Assert.Empty(results);
    }

    private async Task<IReadOnlyList<Candidate>> EvaluateAsync(int? libraryId)
    {
        await using var db = new OptimisarrDbContext(_options);
        return await new CandidateService(db).EvaluateAsync(libraryId, CancellationToken.None);
    }

    private static Candidate Single(IReadOnlyList<Candidate> results, string relativePath) =>
        results.Single(candidate => candidate.RelativePath == relativePath);

    private static MediaFile Probed(int libraryId, string relativePath, string videoCodec, bool isHdr = false) => new()
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
        IsHdr = isHdr,
        ProbedAt = DateTimeOffset.UtcNow
    };

    public void Dispose() => _connection.Dispose();
}
