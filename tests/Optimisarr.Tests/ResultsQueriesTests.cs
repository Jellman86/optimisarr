using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Stats;
using Optimisarr.Core.Domain;
using Optimisarr.Data;

namespace Optimisarr.Tests;

public sealed class ResultsQueriesTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 7, 0, 0, TimeSpan.Zero);
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<OptimisarrDbContext> _options;

    public ResultsQueriesTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<OptimisarrDbContext>().UseSqlite(_connection).Options;
        using var db = new OptimisarrDbContext(_options);
        db.Database.EnsureCreated();
    }

    private static string Report(long original, long output, double vmaf)
    {
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        return "{\"checks\":[{\"name\":\"Size saving\",\"outcome\":\"Passed\",\"detail\":\"Original "
            + original.ToString("n0", invariant) + " bytes, output " + output.ToString("n0", invariant) + " bytes.\"}],"
            + "\"vmaf\":{\"measured\":true,\"error\":null,\"scores\":{\"vmafHarmonicMean\":" + vmaf.ToString(invariant) + "}}}";
    }

    private static async Task<int> SeedLibraryAsync(OptimisarrDbContext db)
    {
        var library = new Library { Name = "TV", Path = "/data/tv", MediaType = MediaType.Tv };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();
        return library.Id;
    }

    // Each job gets its own media file: the job's path and title come from the file it encoded.
    private static Job JobFor(int libraryId, string path, JobStatus status, JobType type = JobType.Normal) => new()
    {
        MediaFile = new MediaFile { LibraryId = libraryId, Path = "/data/tv/" + path, RelativePath = path, SizeBytes = 10 },
        LibraryId = libraryId,
        Status = status,
        Type = type,
    };

    private static Job Completed(int libraryId, string path, long source, long output, DateTimeOffset finished, double vmaf = 91.3)
    {
        var job = JobFor(libraryId, path, JobStatus.Completed);
        job.SourceSizeBytes = source;
        job.OutputSizeBytes = output;
        job.FinishedAt = finished;
        job.VideoEncoder = "hevc_qsv";
        job.VerificationReportJson = Report(source, output, vmaf);
        return job;
    }

    [Fact]
    public async Task Recent_results_are_completed_jobs_with_both_sizes_newest_first()
    {
        await using (var db = new OptimisarrDbContext(_options))
        {
            var libraryId = await SeedLibraryAsync(db);
            db.Jobs.AddRange(
                Completed(libraryId, "old.mkv", 1_000, 400, Now.AddDays(-2)),
                Completed(libraryId, "new.mkv", 2_000, 500, Now.AddHours(-1), vmaf: 93.5),
                Sized(JobFor(libraryId, "failed.mkv", JobStatus.Failed)),
                Sized(JobFor(libraryId, "preview.mkv", JobStatus.Completed, JobType.Preview)),
                JobFor(libraryId, "unsized.mkv", JobStatus.Completed));
            await db.SaveChangesAsync();
        }

        await using var read = new OptimisarrDbContext(_options);
        var recent = await ResultsQueries.RecentAsync(read, take: 10, CancellationToken.None);

        Assert.Equal(["new.mkv", "old.mkv"], recent.Select(r => r.RelativePath));
        var first = recent[0];
        Assert.Equal("TV", first.LibraryName);
        Assert.Equal(2_000, first.SourceSizeBytes);
        Assert.Equal(500, first.OutputSizeBytes);
        Assert.Equal(93.5, first.VmafHarmonicMean);
        Assert.Equal("hevc_qsv", first.VideoEncoder);
    }

    private static Job Sized(Job job)
    {
        job.SourceSizeBytes = 1_000;
        job.OutputSizeBytes = 400;
        job.FinishedAt = Now;
        return job;
    }

    [Fact]
    public async Task Recent_results_respect_the_requested_count()
    {
        await using (var db = new OptimisarrDbContext(_options))
        {
            var libraryId = await SeedLibraryAsync(db);
            for (var i = 0; i < 5; i++) db.Jobs.Add(Completed(libraryId, $"{i}.mkv", 1_000, 400, Now.AddMinutes(-i)));
            await db.SaveChangesAsync();
        }

        await using var read = new OptimisarrDbContext(_options);
        Assert.Equal(3, (await ResultsQueries.RecentAsync(read, take: 3, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task Daily_results_bucket_completed_jobs_into_local_days()
    {
        await using (var db = new OptimisarrDbContext(_options))
        {
            var libraryId = await SeedLibraryAsync(db);
            db.Jobs.AddRange(
                Completed(libraryId, "a.mkv", 1_000, 400, Now.AddHours(-2)),
                Completed(libraryId, "b.mkv", 3_000, 1_000, Now.AddDays(-1)),
                Completed(libraryId, "c.mkv", 9_000, 1_000, Now.AddDays(-40)));
            await db.SaveChangesAsync();
        }

        await using var read = new OptimisarrDbContext(_options);
        var days = await ResultsQueries.DailyAsync(read, days: 30, Now, TimeSpan.Zero, CancellationToken.None);

        Assert.Equal(30, days.Count);
        Assert.Equal(600, days[^1].BytesSaved);
        Assert.Equal(2_000, days[^2].BytesSaved);
        Assert.Equal(2_600, days.Sum(d => d.BytesSaved));
    }

    public void Dispose() => _connection.Dispose();
}
