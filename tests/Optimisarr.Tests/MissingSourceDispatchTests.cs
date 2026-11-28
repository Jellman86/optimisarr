using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Optimisarr.Api.Queue;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Library;
using Optimisarr.Core.Queue;
using Optimisarr.Data;

namespace Optimisarr.Tests;

/// <summary>
/// A job whose file a media manager replaced while it waited ends as skipped, not failed; a job
/// whose whole library has gone still fails, so an outage is never hidden (#610).
/// </summary>
[Collection(TokenedApiCollection.Name)]
public sealed class MissingSourceDispatchTests(AdminTokenAuthEndpointTests.TokenedApi api) : IAsyncLifetime
{
    private readonly List<int> _createdLibraries = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        db.Libraries.RemoveRange(db.Libraries.Where(library => _createdLibraries.Contains(library.Id)));
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_file_replaced_by_a_media_manager_before_its_job_starts_is_skipped_with_a_reason()
    {
        var (jobId, attempt, library, source) = await StartAJobAsync();
        File.Delete(source);
        await File.WriteAllBytesAsync(Path.Combine(library, "Episode WEBDL-1080p.mkv"), [1, 2, 3]);

        await RunAsync(jobId, attempt);

        var job = await ReadJobAsync(jobId);
        Assert.Equal(JobStatus.Cancelled, job.Status);
        Assert.Equal($"Skipped before encoding: {MissingSource.SkipReason}", job.ErrorMessage);
        Assert.Null(job.FailureCategory);
    }

    [Fact]
    public async Task A_job_whose_library_folder_has_gone_still_fails_and_says_which_folder()
    {
        var (jobId, attempt, library, _) = await StartAJobAsync();
        Directory.Delete(library, recursive: true);

        await RunAsync(jobId, attempt);

        var job = await ReadJobAsync(jobId);
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal(MissingSource.LibraryUnavailableMessage(library), job.ErrorMessage);
    }

    [Fact]
    public async Task A_job_whose_library_folder_is_left_empty_by_a_dropped_share_still_fails()
    {
        var (jobId, attempt, library, source) = await StartAJobAsync();
        File.Delete(source);

        await RunAsync(jobId, attempt);

        var job = await ReadJobAsync(jobId);
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal(MissingSource.LibraryUnavailableMessage(library), job.ErrorMessage);
    }

    private async Task RunAsync(int jobId, int attempt)
    {
        using var dispatcher = ActivatorUtilities.CreateInstance<QueueDispatcher>(api.Services);
        await dispatcher.RunJobAsync(jobId, attempt, CancellationToken.None);
    }

    private async Task<Job> ReadJobAsync(int jobId)
    {
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>()
            .Jobs.AsNoTracking().SingleAsync(job => job.Id == jobId);
    }

    /// <summary>A queued episode the local dispatcher has just claimed, as it would be when it starts.</summary>
    private async Task<(int JobId, int Attempt, string Library, string Source)> StartAJobAsync()
    {
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var library = new Library
        {
            Name = "Missing source",
            Path = Path.Combine(api.LibraryDirectory, Guid.NewGuid().ToString("N")),
            WorkPlacement = WorkPlacement.LocalOnly,
        };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();
        _createdLibraries.Add(library.Id);

        Directory.CreateDirectory(library.Path);
        var source = Path.Combine(library.Path, "Episode WEBDL-720p.mkv");
        await File.WriteAllBytesAsync(source, [1, 2, 3, 4]);
        var file = new MediaFile
        {
            LibraryId = library.Id,
            Path = source,
            RelativePath = Path.GetFileName(source),
            SizeBytes = 4,
            MediaKind = MediaKind.Video,
            VideoCodec = "h264",
            PixelFormat = "yuv420p",
            Width = 1280,
            Height = 720,
            DurationSeconds = 100,
        };
        db.MediaFiles.Add(file);
        await db.SaveChangesAsync();

        var job = new Job { MediaFileId = file.Id, LibraryId = library.Id, Status = JobStatus.Queued, Type = JobType.Normal };
        db.Jobs.Add(job);
        await db.SaveChangesAsync();
        QueueDispatcher.PrepareForAttempt(job, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
        return (job.Id, job.ExecutionAttempt, library.Path, source);
    }
}
