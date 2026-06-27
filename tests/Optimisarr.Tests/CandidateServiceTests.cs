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
    public async Task A_file_with_a_completed_job_is_no_longer_offered()
    {
        await using (var db = new OptimisarrDbContext(_options))
        {
            var library = new Library { Name = "Films", Path = "/data/films", RuleProfile = RuleProfile.ConservativeHevc };
            db.Libraries.Add(library);
            await db.SaveChangesAsync();

            var file = Probed(library.Id, "a.mkv", videoCodec: "h264");
            file.ModifiedAt = DateTimeOffset.Parse("2026-06-01T00:00:00Z");
            db.MediaFiles.Add(file);
            await db.SaveChangesAsync();

            // A successful job that finished after the file was last modified.
            db.Jobs.Add(new Job
            {
                MediaFileId = file.Id,
                LibraryId = library.Id,
                Status = JobStatus.Completed,
                FinishedAt = DateTimeOffset.Parse("2026-06-01T01:00:00Z")
            });
            await db.SaveChangesAsync();
        }

        var result = Single(await EvaluateAsync(libraryId: null), "a.mkv");

        Assert.False(result.Eligible);
        Assert.Equal("Already optimised", result.Reason);
    }

    [Fact]
    public async Task A_file_with_a_failed_job_is_held_back_until_retried()
    {
        await using (var db = new OptimisarrDbContext(_options))
        {
            var library = new Library { Name = "Films", Path = "/data/films", RuleProfile = RuleProfile.ConservativeHevc };
            db.Libraries.Add(library);
            await db.SaveChangesAsync();

            var file = Probed(library.Id, "a.mkv", videoCodec: "h264");
            file.ModifiedAt = DateTimeOffset.Parse("2026-06-01T00:00:00Z");
            db.MediaFiles.Add(file);
            await db.SaveChangesAsync();

            db.Jobs.Add(new Job
            {
                MediaFileId = file.Id,
                LibraryId = library.Id,
                Status = JobStatus.Failed,
                FinishedAt = DateTimeOffset.Parse("2026-06-01T01:00:00Z")
            });
            await db.SaveChangesAsync();
        }

        var result = Single(await EvaluateAsync(libraryId: null), "a.mkv");

        Assert.False(result.Eligible);
        Assert.Contains("Previously failed", result.Reason);
    }

    [Fact]
    public async Task A_file_changed_after_its_job_becomes_eligible_again()
    {
        await using (var db = new OptimisarrDbContext(_options))
        {
            var library = new Library { Name = "Films", Path = "/data/films", RuleProfile = RuleProfile.ConservativeHevc };
            db.Libraries.Add(library);
            await db.SaveChangesAsync();

            var file = Probed(library.Id, "a.mkv", videoCodec: "h264");
            file.ModifiedAt = DateTimeOffset.Parse("2026-06-02T00:00:00Z"); // re-ripped after the job
            db.MediaFiles.Add(file);
            await db.SaveChangesAsync();

            db.Jobs.Add(new Job
            {
                MediaFileId = file.Id,
                LibraryId = library.Id,
                Status = JobStatus.Completed,
                FinishedAt = DateTimeOffset.Parse("2026-06-01T01:00:00Z")
            });
            await db.SaveChangesAsync();
        }

        var result = Single(await EvaluateAsync(libraryId: null), "a.mkv");

        Assert.True(result.Eligible);
    }

    [Fact]
    public async Task Skips_an_original_whose_optimised_copy_already_sits_in_the_library()
    {
        await using (var db = new OptimisarrDbContext(_options))
        {
            var library = new Library { Name = "Films", Path = "/data/films", RuleProfile = RuleProfile.ConservativeHevc };
            db.Libraries.Add(library);
            await db.SaveChangesAsync();

            // The h264 original is rule-eligible, but its hevc re-container produced by an earlier
            // pass is still beside it (same stem, marked). It must not be queued to transcode again.
            db.MediaFiles.Add(Probed(library.Id, "Movie.mkv", videoCodec: "h264"));
            db.MediaFiles.Add(Probed(
                library.Id, "Movie.mp4", videoCodec: "hevc", container: "mov,mp4,m4a", optimisedMarker: "0.1.0.0"));
            // A lone original with no optimised sibling stays eligible.
            db.MediaFiles.Add(Probed(library.Id, "Other.mkv", videoCodec: "h264"));
            await db.SaveChangesAsync();
        }

        var results = await EvaluateAsync(libraryId: null);

        var original = Single(results, "Movie.mkv");
        Assert.False(original.Eligible);
        Assert.Contains("optimised copy already exists", original.Reason);
        // The marked copy itself is skipped on its own merits (already hevc), never held back as a
        // sibling of itself, and the unrelated original is still eligible.
        Assert.False(Single(results, "Movie.mp4").Eligible);
        Assert.True(Single(results, "Other.mkv").Eligible);
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

    [Fact]
    public async Task Summary_counts_eligible_and_skipped_per_library()
    {
        int filmsId, musicId;
        await using (var db = new OptimisarrDbContext(_options))
        {
            var films = new Library { Name = "Films", Path = "/data/films", RuleProfile = RuleProfile.ConservativeHevc };
            var music = new Library { Name = "Music", Path = "/data/music", MediaType = MediaType.Music };
            db.Libraries.AddRange(films, music);
            await db.SaveChangesAsync();
            filmsId = films.Id;
            musicId = music.Id;

            db.MediaFiles.Add(Probed(films.Id, "a.mkv", videoCodec: "h264"));   // eligible
            db.MediaFiles.Add(Probed(films.Id, "b.mkv", videoCodec: "h264"));   // eligible
            db.MediaFiles.Add(Probed(films.Id, "c.mkv", videoCodec: "hevc"));   // skipped: already hevc
            // A music library with one lossless (eligible) audio file.
            db.MediaFiles.Add(new MediaFile
            {
                LibraryId = music.Id,
                Path = "/data/music/Album/Track.flac",
                RelativePath = "Album/Track.flac",
                SizeBytes = 40L * 1024 * 1024,
                Status = MediaFileStatus.Probed,
                MediaKind = MediaKind.Audio,
                AudioCodecs = "flac",
                ProbedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        await using var readDb = new OptimisarrDbContext(_options);
        var summary = await new CandidateService(readDb).SummariseAsync(CancellationToken.None);

        var films2 = summary.Single(s => s.LibraryId == filmsId);
        Assert.Equal(2, films2.Eligible);
        Assert.Equal(1, films2.Skipped);

        var music2 = summary.Single(s => s.LibraryId == musicId);
        Assert.Equal(1, music2.Eligible);
        Assert.Equal(0, music2.Skipped);
    }

    [Fact]
    public async Task An_excluded_file_is_skipped_and_becomes_eligible_again_once_removed()
    {
        string path;
        await using (var db = new OptimisarrDbContext(_options))
        {
            var library = new Library { Name = "Films", Path = "/data/films", RuleProfile = RuleProfile.ConservativeHevc };
            db.Libraries.Add(library);
            await db.SaveChangesAsync();

            var file = Probed(library.Id, "a.mkv", videoCodec: "h264");   // otherwise eligible: h264 -> hevc
            db.MediaFiles.Add(file);
            await db.SaveChangesAsync();
            path = file.Path;

            db.Exclusions.Add(new Exclusion { Path = path, LibraryId = library.Id, RelativePath = "a.mkv" });
            await db.SaveChangesAsync();
        }

        var excluded = Single(await EvaluateAsync(libraryId: null), "a.mkv");
        Assert.False(excluded.Eligible);
        Assert.Contains("Excluded", excluded.Reason);

        // Removing the exclusion restores eligibility.
        await using (var db = new OptimisarrDbContext(_options))
        {
            db.Exclusions.Remove(await db.Exclusions.SingleAsync(e => e.Path == path));
            await db.SaveChangesAsync();
        }

        Assert.True(Single(await EvaluateAsync(libraryId: null), "a.mkv").Eligible);
    }

    private async Task<IReadOnlyList<Candidate>> EvaluateAsync(int? libraryId)
    {
        await using var db = new OptimisarrDbContext(_options);
        return await new CandidateService(db).EvaluateAsync(libraryId, CancellationToken.None);
    }

    private static Candidate Single(IReadOnlyList<Candidate> results, string relativePath) =>
        results.Single(candidate => candidate.RelativePath == relativePath);

    private static MediaFile Probed(
        int libraryId, string relativePath, string videoCodec, bool isHdr = false,
        string? container = "matroska,webm", string? optimisedMarker = null) => new()
    {
        LibraryId = libraryId,
        Path = $"/data/films/{relativePath}",
        RelativePath = relativePath,
        SizeBytes = 5_000_000_000,
        Status = MediaFileStatus.Probed,
        Container = container,
        VideoCodec = videoCodec,
        Width = 1920,
        Height = 1080,
        IsHdr = isHdr,
        OptimisedMarker = optimisedMarker,
        ProbedAt = DateTimeOffset.UtcNow
    };

    public void Dispose() => _connection.Dispose();
}
