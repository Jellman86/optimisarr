using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Queue;
using Optimisarr.Data;

namespace Optimisarr.Tests;

/// <summary>
/// Exercises the dispatcher's per-file failure tracking against a real database, without standing
/// up the whole background worker: a file that fails enough times is auto-excluded, and a success
/// clears the streak.
/// </summary>
public sealed class AutoExcludeFailureTrackingTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<OptimisarrDbContext> _options;

    public AutoExcludeFailureTrackingTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:;Foreign Keys=True");
        _connection.Open();
        _options = new DbContextOptionsBuilder<OptimisarrDbContext>().UseSqlite(_connection).Options;
        using var db = new OptimisarrDbContext(_options);
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task A_file_is_auto_excluded_only_after_the_third_failure()
    {
        await using var db = new OptimisarrDbContext(_options);
        var (job, file) = await SeedAsync(db);

        // First two failures: counted, but not yet excluded.
        await QueueDispatcher.ApplyFailureTrackingAsync(db, job, JobStatus.Failed);
        await QueueDispatcher.ApplyFailureTrackingAsync(db, job, JobStatus.Failed);
        await db.SaveChangesAsync();
        Assert.Equal(2, (await db.MediaFiles.SingleAsync()).FailureCount);
        Assert.Empty(db.Exclusions);

        // Third failure crosses the threshold and auto-excludes the file.
        await QueueDispatcher.ApplyFailureTrackingAsync(db, job, JobStatus.Failed);
        await db.SaveChangesAsync();

        var exclusion = Assert.Single(db.Exclusions);
        Assert.Equal(ExclusionSource.RepeatedFailures, exclusion.Source);
        Assert.Equal(file.Path, exclusion.Path);
        Assert.Contains("3 failed attempts", exclusion.Reason);
    }

    [Fact]
    public async Task A_success_clears_the_failure_streak()
    {
        await using var db = new OptimisarrDbContext(_options);
        var (job, _) = await SeedAsync(db);

        await QueueDispatcher.ApplyFailureTrackingAsync(db, job, JobStatus.Failed);
        await QueueDispatcher.ApplyFailureTrackingAsync(db, job, JobStatus.Failed);
        await QueueDispatcher.ApplyFailureTrackingAsync(db, job, JobStatus.Completed);
        await db.SaveChangesAsync();

        Assert.Equal(0, (await db.MediaFiles.SingleAsync()).FailureCount);
        Assert.Empty(db.Exclusions);
    }

    [Fact]
    public async Task An_already_excluded_file_is_not_excluded_twice()
    {
        await using var db = new OptimisarrDbContext(_options);
        var (job, file) = await SeedAsync(db);
        db.Exclusions.Add(new Exclusion { Path = file.Path, Source = ExclusionSource.Manual });
        await db.SaveChangesAsync();

        for (var i = 0; i < 5; i++)
        {
            await QueueDispatcher.ApplyFailureTrackingAsync(db, job, JobStatus.Failed);
        }
        await db.SaveChangesAsync();

        Assert.Single(db.Exclusions);   // still just the original manual one
    }

    [Theory]
    [InlineData(JobType.Preview)]
    [InlineData(JobType.Calibration)]
    public async Task Disposable_comparison_work_never_changes_a_files_failure_streak(JobType type)
    {
        await using var db = new OptimisarrDbContext(_options);
        var (job, file) = await SeedAsync(db);
        job.Type = type;
        file.FailureCount = 2;

        await QueueDispatcher.ApplyFailureTrackingAsync(db, job, JobStatus.Failed);
        await QueueDispatcher.ApplyFailureTrackingAsync(db, job, JobStatus.Completed);
        await db.SaveChangesAsync();

        Assert.Equal(2, (await db.MediaFiles.SingleAsync()).FailureCount);
        Assert.Empty(db.Exclusions);
    }

    private static async Task<(Job Job, MediaFile File)> SeedAsync(OptimisarrDbContext db)
    {
        var library = new Library { Name = "Films", Path = "/data/films" };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();

        var file = new MediaFile
        {
            LibraryId = library.Id,
            Path = "/data/films/big.mkv",
            RelativePath = "big.mkv",
            SizeBytes = 9_000_000_000,
            Status = MediaFileStatus.Probed
        };
        db.MediaFiles.Add(file);
        await db.SaveChangesAsync();

        var job = new Job { MediaFileId = file.Id, LibraryId = library.Id, Status = JobStatus.Transcoding };
        db.Jobs.Add(job);
        await db.SaveChangesAsync();
        return (job, file);
    }

    public void Dispose() => _connection.Dispose();
}
