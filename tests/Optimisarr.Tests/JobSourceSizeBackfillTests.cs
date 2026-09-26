using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Stats;
using Optimisarr.Core.Domain;
using Optimisarr.Data;

namespace Optimisarr.Tests;

public sealed class JobSourceSizeBackfillTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<OptimisarrDbContext> _options;

    public JobSourceSizeBackfillTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<OptimisarrDbContext>().UseSqlite(_connection).Options;
        using var db = new OptimisarrDbContext(_options);
        db.Database.EnsureCreated();
    }

    private const string LegacyReport =
        """{"checks":[{"name":"Size saving","outcome":"Passed","detail":"Original 1,282,683,553 bytes, output 368,922,428 bytes (-71.2% change)."}]}""";

    private async Task<int> SeedFileAsync(OptimisarrDbContext db)
    {
        var library = new Library { Name = "TV", Path = "/data/tv", MediaType = MediaType.Tv };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();
        var file = new MediaFile { LibraryId = library.Id, Path = "/data/tv/a.mkv", RelativePath = "a.mkv", SizeBytes = 10 };
        db.MediaFiles.Add(file);
        await db.SaveChangesAsync();
        return file.Id;
    }

    [Fact]
    public async Task Jobs_verified_before_the_column_existed_get_their_source_size_from_the_report()
    {
        int legacy, known, unverified;
        await using (var db = new OptimisarrDbContext(_options))
        {
            var fileId = await SeedFileAsync(db);
            var legacyJob = new Job { MediaFileId = fileId, Status = JobStatus.Completed, VerificationReportJson = LegacyReport };
            var knownJob = new Job { MediaFileId = fileId, Status = JobStatus.Completed, SourceSizeBytes = 77, VerificationReportJson = LegacyReport };
            var unverifiedJob = new Job { MediaFileId = fileId, Status = JobStatus.Queued };
            db.Jobs.AddRange(legacyJob, knownJob, unverifiedJob);
            await db.SaveChangesAsync();
            (legacy, known, unverified) = (legacyJob.Id, knownJob.Id, unverifiedJob.Id);
        }

        await using (var db = new OptimisarrDbContext(_options))
        {
            Assert.Equal(1, await JobSourceSizeBackfill.FillFromReportsAsync(db, CancellationToken.None));
        }

        await using var read = new OptimisarrDbContext(_options);
        Assert.Equal(1_282_683_553, (await read.Jobs.FindAsync(legacy))!.SourceSizeBytes);
        Assert.Equal(77, (await read.Jobs.FindAsync(known))!.SourceSizeBytes);
        Assert.Null((await read.Jobs.FindAsync(unverified))!.SourceSizeBytes);
    }

    [Fact]
    public async Task The_backfill_runs_once()
    {
        await using (var db = new OptimisarrDbContext(_options))
        {
            var fileId = await SeedFileAsync(db);
            db.Jobs.Add(new Job { MediaFileId = fileId, Status = JobStatus.Completed, VerificationReportJson = LegacyReport });
            await db.SaveChangesAsync();
            Assert.Equal(1, await JobSourceSizeBackfill.FillFromReportsAsync(db, CancellationToken.None));
        }

        await using (var db = new OptimisarrDbContext(_options))
        {
            var fileId = await db.MediaFiles.Select(f => f.Id).FirstAsync();
            db.Jobs.Add(new Job { MediaFileId = fileId, Status = JobStatus.Completed, VerificationReportJson = LegacyReport });
            await db.SaveChangesAsync();
            Assert.Equal(0, await JobSourceSizeBackfill.FillFromReportsAsync(db, CancellationToken.None));
        }
    }

    public void Dispose() => _connection.Dispose();
}
