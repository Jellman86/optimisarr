using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Library;
using Optimisarr.Core.Domain;
using Optimisarr.Data;

namespace Optimisarr.Tests;

public sealed class JobEnqueueServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<OptimisarrDbContext> _options;

    public JobEnqueueServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:;Foreign Keys=True");
        _connection.Open();
        _options = new DbContextOptionsBuilder<OptimisarrDbContext>().UseSqlite(_connection).Options;
        using var db = new OptimisarrDbContext(_options);
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task Enqueues_eligible_files_and_skips_ineligible()
    {
        var libraryId = await SeedLibraryWithFilesAsync();

        var result = await EnqueueAsync(libraryId);

        Assert.Equal(1, result.Enqueued);    // only the h264 file
        Assert.Equal(1, result.Ineligible);  // the already-hevc file

        await using var db = new OptimisarrDbContext(_options);
        var job = Assert.Single(db.Jobs);
        Assert.Equal(JobStatus.Queued, job.Status);
        Assert.Equal(2, job.Priority);       // snapshotted from the library
    }

    [Fact]
    public async Task Is_idempotent_for_files_with_an_active_job()
    {
        var libraryId = await SeedLibraryWithFilesAsync();

        var first = await EnqueueAsync(libraryId);
        var second = await EnqueueAsync(libraryId);

        Assert.Equal(1, first.Enqueued);
        Assert.Equal(0, second.Enqueued);
        Assert.Equal(1, second.AlreadyQueued);

        await using var db = new OptimisarrDbContext(_options);
        Assert.Single(db.Jobs);
    }

    private async Task<int> SeedLibraryWithFilesAsync()
    {
        await using var db = new OptimisarrDbContext(_options);
        var library = new Library
        {
            Name = "Films",
            Path = "/data/films",
            RuleProfile = RuleProfile.ConservativeHevc,
            Priority = 2
        };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();

        db.MediaFiles.Add(Probed(library.Id, "a.mkv", "h264"));   // eligible -> hevc
        db.MediaFiles.Add(Probed(library.Id, "b.mkv", "hevc"));   // already hevc -> skipped
        await db.SaveChangesAsync();
        return library.Id;
    }

    private async Task<EnqueueResult> EnqueueAsync(int libraryId)
    {
        await using var db = new OptimisarrDbContext(_options);
        var library = await db.Libraries.SingleAsync(l => l.Id == libraryId);
        var service = new JobEnqueueService(db, new CandidateService(db));
        return await service.EnqueueEligibleAsync(library, CancellationToken.None);
    }

    private static MediaFile Probed(int libraryId, string relativePath, string videoCodec) => new()
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
        ProbedAt = DateTimeOffset.UtcNow
    };

    public void Dispose() => _connection.Dispose();
}
