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

    public void Dispose() => _connection.Dispose();
}
