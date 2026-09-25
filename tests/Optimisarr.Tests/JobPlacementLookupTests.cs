using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Workers;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Queue;
using Optimisarr.Data;

namespace Optimisarr.Tests;

/// <summary>
/// The placement read is the first half of every hand-back decision, and its failure mode is
/// silence: a job whose library cannot be reached answers with the enum's default rather than
/// admitting it found nothing. These run against a real SQLite context because the distinction
/// lives in the translated query, not in the calling code.
/// </summary>
public sealed class JobPlacementLookupTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<OptimisarrDbContext> _options;

    public JobPlacementLookupTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<OptimisarrDbContext>().UseSqlite(_connection).Options;
        using var db = new OptimisarrDbContext(_options);
        db.Database.EnsureCreated();
    }

    [Theory]
    [InlineData(WorkPlacement.Anywhere)]
    [InlineData(WorkPlacement.LocalOnly)]
    [InlineData(WorkPlacement.PreferWorker)]
    [InlineData(WorkPlacement.WorkerOnly)]
    public async Task Reports_the_placement_its_library_asks_for(WorkPlacement placement)
    {
        var jobId = await SeedAsync(placement, JobType.Normal);

        await using var db = new OptimisarrDbContext(_options);
        Assert.Equal(placement, await JobPlacementLookup.ForJobAsync(db, jobId, CancellationToken.None));
    }

    [Fact]
    public async Task A_job_that_is_not_there_is_unresolved_rather_than_anywhere()
    {
        // Anywhere is the enum's default, so this is the case a non-nullable projection gets wrong:
        // it would report a deleted job as a library that is happy to run here.
        await SeedAsync(WorkPlacement.WorkerOnly, JobType.Normal);

        await using var db = new OptimisarrDbContext(_options);
        Assert.Null(await JobPlacementLookup.ForJobAsync(db, 9999, CancellationToken.None));
    }

    [Theory]
    [InlineData(JobType.Preview)]
    [InlineData(JobType.Calibration)]
    public async Task Only_a_normal_job_has_a_placement_to_read(JobType type)
    {
        // Previews and calibrations belong to this machine whatever the library prefers, so the
        // lookup declines to answer for them rather than handing back a placement to act on.
        var jobId = await SeedAsync(WorkPlacement.WorkerOnly, type);

        await using var db = new OptimisarrDbContext(_options);
        Assert.Null(await JobPlacementLookup.ForJobAsync(db, jobId, CancellationToken.None));
    }

    private async Task<int> SeedAsync(WorkPlacement placement, JobType type)
    {
        await using var db = new OptimisarrDbContext(_options);
        var library = new Library
        {
            Name = "Films",
            Path = "/data/film",
            MediaType = MediaType.Film,
            WorkPlacement = placement,
        };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();

        var file = new MediaFile
        {
            LibraryId = library.Id,
            Path = "/data/film/x.mkv",
            RelativePath = "x.mkv",
            SizeBytes = 1000,
        };
        db.MediaFiles.Add(file);
        await db.SaveChangesAsync();

        var job = new Job { MediaFileId = file.Id, Type = type, Status = JobStatus.Queued };
        db.Jobs.Add(job);
        await db.SaveChangesAsync();
        return job.Id;
    }

    public void Dispose() => _connection.Dispose();
}
