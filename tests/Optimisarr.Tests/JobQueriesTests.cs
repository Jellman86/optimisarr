using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Queue;
using Optimisarr.Data;

namespace Optimisarr.Tests;

public sealed class JobQueriesTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<OptimisarrDbContext> _options;

    public JobQueriesTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:;Foreign Keys=True");
        _connection.Open();
        _options = new DbContextOptionsBuilder<OptimisarrDbContext>().UseSqlite(_connection).Options;
        using var db = new OptimisarrDbContext(_options);
        db.Database.EnsureCreated();
    }

    // Regression: SQLite cannot ORDER BY a DateTimeOffset, so listing must order the
    // materialised rows client-side. A SQL-side ThenBy(EnqueuedAt) throws at query time.
    [Fact]
    public async Task ListAsync_orders_by_priority_then_enqueued_without_throwing()
    {
        var baseTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await using (var db = new OptimisarrDbContext(_options))
        {
            var library = new Library { Name = "Films", Path = "/data/films" };
            db.Libraries.Add(library);
            await db.SaveChangesAsync();

            // Each job needs a media file (required FK); ids line up for clarity.
            db.MediaFiles.Add(MediaFile(library.Id, id: 1));
            db.MediaFiles.Add(MediaFile(library.Id, id: 2));
            db.MediaFiles.Add(MediaFile(library.Id, id: 3));
            await db.SaveChangesAsync();

            db.Jobs.Add(Job(id: 1, priority: 1, enqueuedAt: baseTime));
            db.Jobs.Add(Job(id: 2, priority: 5, enqueuedAt: baseTime.AddMinutes(10)));
            db.Jobs.Add(Job(id: 3, priority: 5, enqueuedAt: baseTime.AddMinutes(5)));
            await db.SaveChangesAsync();
        }

        await using var readDb = new OptimisarrDbContext(_options);
        var jobs = await JobQueries.ListAsync(readDb, CancellationToken.None);

        // Highest priority first; within a priority, oldest enqueued first.
        Assert.Equal(new[] { 3, 2, 1 }, jobs.Select(j => j.Id));
    }

    [Fact]
    public async Task ListAsync_surfaces_the_resolved_video_encoder()
    {
        await using (var db = new OptimisarrDbContext(_options))
        {
            var library = new Library { Name = "Films", Path = "/data/films" };
            db.Libraries.Add(library);
            await db.SaveChangesAsync();
            db.MediaFiles.Add(MediaFile(library.Id, id: 1));
            await db.SaveChangesAsync();
            var job = Job(id: 1, priority: 1, enqueuedAt: DateTimeOffset.UtcNow);
            job.VideoEncoder = "hevc_nvenc";
            db.Jobs.Add(job);
            await db.SaveChangesAsync();
        }

        await using var readDb = new OptimisarrDbContext(_options);
        var jobs = await JobQueries.ListAsync(readDb, CancellationToken.None);

        Assert.Equal("hevc_nvenc", Assert.Single(jobs).VideoEncoder);
    }

    [Fact]
    public async Task ListAsync_excludes_preview_jobs()
    {
        await using (var db = new OptimisarrDbContext(_options))
        {
            var library = new Library { Name = "Films", Path = "/data/films" };
            db.Libraries.Add(library);
            await db.SaveChangesAsync();
            db.MediaFiles.Add(MediaFile(library.Id, id: 1));
            db.MediaFiles.Add(MediaFile(library.Id, id: 2));
            await db.SaveChangesAsync();

            db.Jobs.Add(Job(id: 1, priority: 1, enqueuedAt: DateTimeOffset.UtcNow));
            var preview = Job(id: 2, priority: 1, enqueuedAt: DateTimeOffset.UtcNow);
            preview.Type = JobType.Preview;
            db.Jobs.Add(preview);
            await db.SaveChangesAsync();
        }

        await using var readDb = new OptimisarrDbContext(_options);
        var jobs = await JobQueries.ListAsync(readDb, CancellationToken.None);

        // The preview job is hidden from the normal queue list.
        Assert.Equal(1, Assert.Single(jobs).Id);
    }

    [Fact]
    public async Task ListAsync_narrows_to_a_single_status_when_one_is_given()
    {
        await using (var db = new OptimisarrDbContext(_options))
        {
            var library = new Library { Name = "Films", Path = "/data/films" };
            db.Libraries.Add(library);
            await db.SaveChangesAsync();
            db.MediaFiles.Add(MediaFile(library.Id, id: 1));
            db.MediaFiles.Add(MediaFile(library.Id, id: 2));
            db.MediaFiles.Add(MediaFile(library.Id, id: 3));
            await db.SaveChangesAsync();

            db.Jobs.Add(Failed(id: 1, "Verification failed: Size saving"));
            db.Jobs.Add(Failed(id: 2, "Verification failed: A/V sync"));
            db.Jobs.Add(Job(id: 3, priority: 1, enqueuedAt: DateTimeOffset.UtcNow));   // still Queued
            await db.SaveChangesAsync();
        }

        await using var readDb = new OptimisarrDbContext(_options);
        var failed = await JobQueries.ListAsync(readDb, CancellationToken.None, JobStatus.Failed);

        Assert.Equal(new[] { 1, 2 }, failed.Select(job => job.Id).OrderBy(id => id));
        Assert.All(failed, job => Assert.Equal("Failed", job.Status));
    }

    [Fact]
    public async Task SummariseFailuresAsync_groups_failures_by_classified_reason_largest_first()
    {
        await using (var db = new OptimisarrDbContext(_options))
        {
            var library = new Library { Name = "Films", Path = "/data/films" };
            db.Libraries.Add(library);
            await db.SaveChangesAsync();
            for (var id = 1; id <= 4; id++)
            {
                db.MediaFiles.Add(MediaFile(library.Id, id));
            }
            await db.SaveChangesAsync();

            db.Jobs.Add(Failed(id: 1, "Verification failed: Size saving"));
            db.Jobs.Add(Failed(id: 2, "Verification failed: Size saving"));
            db.Jobs.Add(Failed(id: 3, "Could not find tag for codec none in stream #37"));
            db.Jobs.Add(Job(id: 4, priority: 1, enqueuedAt: DateTimeOffset.UtcNow));   // not failed, ignored
            await db.SaveChangesAsync();
        }

        await using var readDb = new OptimisarrDbContext(_options);
        var groups = await JobQueries.SummariseFailuresAsync(readDb, CancellationToken.None);

        Assert.Equal(2, groups.Count);
        // Largest group first: two size-saving failures, then one container-incompatibility.
        Assert.Equal("SizeSaving", groups[0].Category);
        Assert.Equal(2, groups[0].Count);
        Assert.Equal(2, groups[0].Samples.Count);
        Assert.Equal("ContainerIncompatibility", groups[1].Category);
        Assert.Equal(1, groups[1].Count);
        Assert.False(string.IsNullOrWhiteSpace(groups[0].Description));
    }

    private static MediaFile MediaFile(int libraryId, int id) => new()
    {
        Id = id,
        LibraryId = libraryId,
        Path = $"/data/films/{id}.mkv",
        RelativePath = $"{id}.mkv",
        SizeBytes = 1_000_000,
        Status = MediaFileStatus.Probed
    };

    private static Job Job(int id, int priority, DateTimeOffset enqueuedAt) => new()
    {
        Id = id,
        MediaFileId = id,
        Priority = priority,
        Status = JobStatus.Queued,
        EnqueuedAt = enqueuedAt
    };

    private static Job Failed(int id, string error) => new()
    {
        Id = id,
        MediaFileId = id,
        Priority = 1,
        Status = JobStatus.Failed,
        ErrorMessage = error,
        EnqueuedAt = DateTimeOffset.UtcNow,
        FinishedAt = DateTimeOffset.UtcNow
    };

    public void Dispose() => _connection.Dispose();
}
