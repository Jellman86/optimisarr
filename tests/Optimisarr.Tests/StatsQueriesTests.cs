using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Stats;
using Optimisarr.Core.Domain;
using Optimisarr.Data;

namespace Optimisarr.Tests;

public sealed class StatsQueriesTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<OptimisarrDbContext> _options;

    public StatsQueriesTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<OptimisarrDbContext>().UseSqlite(_connection).Options;
        using var db = new OptimisarrDbContext(_options);
        db.Database.EnsureCreated();
    }

    [Fact]
    public void Saving_percent_is_size_weighted_and_safe_at_zero()
    {
        Assert.Equal(0.0, StatsQueries.SavingPercent(0, 0));
        Assert.Equal(50.0, StatsQueries.SavingPercent(1000, 500));
    }

    [Fact]
    public async Task Headline_savings_come_from_the_lifetime_tally_quarantine_from_rows()
    {
        await using (var db = CreateDb())
        {
            var library = new Library { Name = "Films", Path = "/data/film", MediaType = MediaType.Film };
            db.Libraries.Add(library);
            await db.SaveChangesAsync();
            var fileId = await AddFileAsync(db, library.Id);

            // Only still-reversible (Replaced) rows feed the "awaiting review / reclaimable" figures;
            // the headline savings are independent of which rows currently exist.
            db.Replacements.AddRange(
                await ReplacementAsync(db, fileId, 1000, 400, ReplacementStatus.Replaced),
                await ReplacementAsync(db, fileId, 2000, 600, ReplacementStatus.Purged));
            await db.SaveChangesAsync();
        }

        await using var readDb = CreateDb();
        var lifetime = new LifetimeStats(FilesOptimised: 2, OriginalBytes: 3000, OptimisedBytes: 1000);
        var stats = await StatsQueries.GetAsync(readDb, lifetime, CancellationToken.None);

        // Headline reflects the durable tally, not a sum over current rows.
        Assert.Equal(2000, stats.BytesSaved);
        Assert.Equal(2, stats.FilesOptimised);
        Assert.Equal(3000, stats.OriginalBytes);
        // One Replaced entry is still in quarantine; its 1000 bytes are reclaimable on approve.
        Assert.Equal(1, stats.InQuarantine);
        Assert.Equal(1000, stats.QuarantineReclaimableBytes);
    }

    [Fact]
    public async Task Counts_jobs_by_lifecycle_state_and_libraries()
    {
        await using (var db = CreateDb())
        {
            var on = new Library { Name = "On", Path = "/a", MediaType = MediaType.Film, Enabled = true };
            var off = new Library { Name = "Off", Path = "/b", MediaType = MediaType.Tv, Enabled = false };
            db.Libraries.AddRange(on, off);
            await db.SaveChangesAsync();
            var fileId = await AddFileAsync(db, on.Id);

            foreach (var status in new[]
                     {
                         JobStatus.Queued, JobStatus.Queued, JobStatus.Transcoding,
                         JobStatus.ReadyToReplace, JobStatus.Failed,
                     })
            {
                db.Jobs.Add(new Job { MediaFileId = fileId, Status = status });
            }
            db.Jobs.Add(new Job { MediaFileId = fileId, Type = JobType.Preview, Status = JobStatus.Queued });
            db.Jobs.Add(new Job { MediaFileId = fileId, Type = JobType.Calibration, Status = JobStatus.Transcoding });
            db.Jobs.Add(new Job { MediaFileId = fileId, Type = JobType.Calibration, Status = JobStatus.Failed });
            await db.SaveChangesAsync();
        }

        await using var readDb = CreateDb();
        var stats = await StatsQueries.GetAsync(readDb, LifetimeStats.Empty, CancellationToken.None);

        Assert.Equal(2, stats.Queued);
        Assert.Equal(1, stats.Running);
        Assert.Equal(1, stats.ReadyToReplace);
        Assert.Equal(1, stats.Failed);
        Assert.Equal(2, stats.Libraries);
        Assert.Equal(1, stats.EnabledLibraries);
    }

    private static async Task<int> AddFileAsync(OptimisarrDbContext db, int libraryId)
    {
        var file = new MediaFile
        {
            LibraryId = libraryId,
            Path = "/data/film/x.mkv",
            RelativePath = "x.mkv",
            SizeBytes = 1000,
        };
        db.MediaFiles.Add(file);
        await db.SaveChangesAsync();
        return file.Id;
    }

    private static async Task<Replacement> ReplacementAsync(
        OptimisarrDbContext db, int fileId, long originalBytes, long newBytes, ReplacementStatus status)
    {
        var job = new Job { MediaFileId = fileId, Status = JobStatus.Completed };
        db.Jobs.Add(job);
        await db.SaveChangesAsync();
        return new Replacement
        {
            JobId = job.Id,
            MediaFileId = fileId,
            OriginalPath = "/data/film/x.mkv",
            QuarantinePath = "/trash/x.mkv",
            FinalPath = "/data/film/x.mkv",
            OriginalSizeBytes = originalBytes,
            NewSizeBytes = newBytes,
            Status = status,
        };
    }

    private OptimisarrDbContext CreateDb() => new(_options);

    public void Dispose() => _connection.Dispose();
}
