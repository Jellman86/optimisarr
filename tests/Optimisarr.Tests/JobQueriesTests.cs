using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Queue;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Verification;
using Optimisarr.Core.Workers;
using Optimisarr.Data;

namespace Optimisarr.Tests;

public sealed class JobQueriesTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<OptimisarrDbContext> _options;

    [Theory]
    [InlineData(JobStatus.ReadyToReplace)]
    [InlineData(JobStatus.Completed)]
    [InlineData(JobStatus.Failed)]
    public async Task Delivered_jobs_keep_worker_attribution_after_verification(JobStatus status)
    {
        await using var db = new OptimisarrDbContext(_options);
        var library = new Library { Name = "Films", Path = "/data/films" };
        var worker = new Worker { Name = "Encoder Mac" };
        db.AddRange(library, worker);
        await db.SaveChangesAsync();
        db.MediaFiles.Add(MediaFile(library.Id, 1));
        await db.SaveChangesAsync();
        var job = Job(1, 1, DateTimeOffset.UtcNow);
        job.Status = status;
        db.Jobs.Add(job);
        await db.SaveChangesAsync();
        db.JobLeases.Add(new JobLease
        {
            Id = Guid.NewGuid(), JobId = job.Id, WorkerId = worker.Id,
            AcquiredAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1),
            State = LeaseState.Completed, Stage = RemoteStage.Delivering,
            VerificationContractJson = "{}"
        });
        await db.SaveChangesAsync();

        var result = Assert.Single(await JobQueries.ListAsync(db, CancellationToken.None));
        Assert.Equal(worker.Name, result.WorkerName);
        Assert.Null(result.RemoteStage);
        Assert.True(result.SidecarVerification);
    }

    public JobQueriesTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:;Foreign Keys=True");
        _connection.Open();
        _options = new DbContextOptionsBuilder<OptimisarrDbContext>().UseSqlite(_connection).Options;
        using var db = new OptimisarrDbContext(_options);
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task Restarted_dispatch_uses_the_latest_delivered_attempt_for_its_verification_lane()
    {
        await using (var db = new OptimisarrDbContext(_options))
        {
            var library = new Library { Name = "Films", Path = "/data/films" };
            var worker = new Worker { Name = "Sidecar" };
            db.AddRange(library, worker);
            await db.SaveChangesAsync();
            db.MediaFiles.AddRange(MediaFile(library.Id, 1), MediaFile(library.Id, 2));
            await db.SaveChangesAsync();
            var strict = Job(1, 0, DateTimeOffset.UtcNow);
            var legacy = Job(2, 0, DateTimeOffset.UtcNow);
            strict.Status = legacy.Status = JobStatus.AwaitingVerification;
            db.Jobs.AddRange(strict, legacy);
            await db.SaveChangesAsync();
            var baseTime = DateTimeOffset.UtcNow.AddMinutes(-2);
            db.JobLeases.AddRange(
                new JobLease { Id = Guid.NewGuid(), JobId = 1, WorkerId = worker.Id,
                    AcquiredAt = baseTime, ExpiresAt = baseTime.AddMinutes(1),
                    State = LeaseState.Completed, VerificationContractJson = null },
                new JobLease { Id = Guid.NewGuid(), JobId = 1, WorkerId = worker.Id,
                    AcquiredAt = baseTime.AddMinutes(1), ExpiresAt = baseTime.AddMinutes(2),
                    State = LeaseState.Completed, VerificationContractJson = "{}" },
                new JobLease { Id = Guid.NewGuid(), JobId = 2, WorkerId = worker.Id,
                    AcquiredAt = baseTime.AddMinutes(1), ExpiresAt = baseTime.AddMinutes(2),
                    State = LeaseState.Completed });
            await db.SaveChangesAsync();
        }

        await using var reopened = new OptimisarrDbContext(_options);
        Assert.Equal([(1, WorkloadLane.Evidence), (2, WorkloadLane.Video)],
            await QueueDispatcher.DeliveredWorkloadsAsync(reopened, CancellationToken.None));
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
    public async Task A_rejected_remote_attempt_survives_restart_and_the_feed_shows_only_the_current_candidate()
    {
        await using (var db = new OptimisarrDbContext(_options))
        {
            var library = new Library { Name = "Films", Path = "/data/films" };
            db.Libraries.Add(library);
            await db.SaveChangesAsync();
            db.MediaFiles.Add(MediaFile(library.Id, id: 1));
            await db.SaveChangesAsync();
            var job = Job(id: 1, priority: 1, enqueuedAt: DateTimeOffset.UtcNow);
            job.Status = JobStatus.Verifying;
            job.ExecutionAttempt = 1;
            job.VideoEncoder = "hevc_videotoolbox";
            job.VerificationPassed = false;
            job.VerificationReportJson = "{\"checks\":[]}";
            job.VerifiedAt = DateTimeOffset.UtcNow;
            db.Jobs.Add(job);
            JobAttemptHistory.RequeueAfterRejectedCandidate(job, "MacBook Air", "videotoolbox", DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        await using var reopened = new OptimisarrDbContext(_options);
        var current = Assert.Single(await JobQueries.ListAsync(reopened, CancellationToken.None));
        Assert.Null(current.VerificationPassed);
        Assert.Null(current.VerificationReportJson);
        Assert.Null(current.VerifiedAt);
        Assert.Null(current.VideoEncoder);
        Assert.Equal("SoftwareDecode", current.RetryReason);
        Assert.Equal("MacBook Air", Assert.Single(JobAttemptHistory.Read(current.AttemptHistoryJson)).WorkerName);

        var picard = new Worker { Name = "PICARD" };
        reopened.Workers.Add(picard);
        await reopened.SaveChangesAsync();
        var claimed = await reopened.Jobs.SingleAsync();
        claimed.Status = JobStatus.AwaitingVerification;
        claimed.ExecutionAttempt += 1;
        claimed.VideoEncoder = "hevc_nvenc";
        reopened.JobLeases.Add(new JobLease
        {
            Id = Guid.NewGuid(), JobId = claimed.Id, WorkerId = picard.Id,
            AcquiredAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1),
            State = LeaseState.Completed
        });
        await reopened.SaveChangesAsync();

        var retried = Assert.Single(await JobQueries.ListAsync(reopened, CancellationToken.None));
        Assert.Equal("PICARD", retried.WorkerName);
        Assert.False(retried.SidecarVerification);
        Assert.Equal("hevc_nvenc", retried.VideoEncoder);
        Assert.Equal(2, retried.ExecutionAttempt);
        Assert.Null(retried.VerificationPassed);
        Assert.Equal("MacBook Air", Assert.Single(JobAttemptHistory.Read(retried.AttemptHistoryJson)).WorkerName);

        claimed.Status = JobStatus.Verifying;
        claimed.StartedAt = DateTimeOffset.UtcNow.AddMinutes(2);
        claimed.VideoEncoder = "libx265";
        await reopened.SaveChangesAsync();
        var localRetry = Assert.Single(await JobQueries.ListAsync(reopened, CancellationToken.None));
        Assert.Null(localRetry.WorkerName);
        Assert.Equal("libx265", localRetry.VideoEncoder);
    }

    [Fact]
    public async Task ListAsync_surfaces_why_a_job_was_enqueued()
    {
        await using (var db = new OptimisarrDbContext(_options))
        {
            var library = new Library { Name = "Films", Path = "/data/films" };
            db.Libraries.Add(library);
            await db.SaveChangesAsync();
            db.MediaFiles.Add(MediaFile(library.Id, id: 1));
            await db.SaveChangesAsync();
            var job = Job(id: 1, priority: 1, enqueuedAt: DateTimeOffset.UtcNow);
            job.EnqueueReason = "h264 → hevc";
            db.Jobs.Add(job);
            await db.SaveChangesAsync();
        }

        await using var readDb = new OptimisarrDbContext(_options);
        var jobs = await JobQueries.ListAsync(readDb, CancellationToken.None);

        Assert.Equal("h264 → hevc", Assert.Single(jobs).EnqueueReason);
    }

    [Fact]
    public async Task ListAsync_excludes_disposable_comparison_jobs()
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

            db.Jobs.Add(Job(id: 1, priority: 1, enqueuedAt: DateTimeOffset.UtcNow));
            var preview = Job(id: 2, priority: 1, enqueuedAt: DateTimeOffset.UtcNow);
            preview.Type = JobType.Preview;
            db.Jobs.Add(preview);
            var calibration = Job(id: 3, priority: 1, enqueuedAt: DateTimeOffset.UtcNow);
            calibration.Type = JobType.Calibration;
            db.Jobs.Add(calibration);
            await db.SaveChangesAsync();
        }

        await using var readDb = new OptimisarrDbContext(_options);
        var jobs = await JobQueries.ListAsync(readDb, CancellationToken.None);

        // Interactive previews and blind-calibration clips are hidden from the normal queue list.
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

    [Fact]
    public async Task SummariseFailuresAsync_prefers_the_stored_category_over_the_message()
    {
        await using (var db = new OptimisarrDbContext(_options))
        {
            var library = new Library { Name = "Films", Path = "/data/films" };
            db.Libraries.Add(library);
            await db.SaveChangesAsync();
            db.MediaFiles.Add(MediaFile(library.Id, id: 1));
            await db.SaveChangesAsync();

            // The message would classify as size-saving, but the stored category wins (e.g. the
            // message was later edited). Proves grouping trusts the persisted class.
            var job = Failed(id: 1, "Verification failed: Size saving");
            job.FailureCategory = FailureCategory.Verification;
            db.Jobs.Add(job);
            await db.SaveChangesAsync();
        }

        await using var readDb = new OptimisarrDbContext(_options);
        var groups = await JobQueries.SummariseFailuresAsync(readDb, CancellationToken.None);

        Assert.Equal("Verification", Assert.Single(groups).Category);
    }

    [Fact]
    public async Task SummariseFailuresAsync_exposes_disposable_comparison_verification_details()
    {
        await using (var db = new OptimisarrDbContext(_options))
        {
            var library = new Library { Name = "Films", Path = "/data/films" };
            db.Libraries.Add(library);
            await db.SaveChangesAsync();
            db.MediaFiles.Add(MediaFile(library.Id, id: 1));
            await db.SaveChangesAsync();

            var job = Failed(id: 1, "Verification failed: Duration; Tail integrity");
            job.Type = JobType.Calibration;
            job.LibraryId = null;
            job.VerificationReportJson =
                """{"checks":[{"name":"Duration","outcome":"Failed","detail":"Original 17.4s, output 12s."},{"name":"Tail integrity","outcome":"Failed","detail":"Output video ends at 11.96s of the source's 17.4s."}]}""";
            db.Jobs.Add(job);
            await db.SaveChangesAsync();
        }

        await using var readDb = new OptimisarrDbContext(_options);
        var group = Assert.Single(await JobQueries.SummariseFailuresAsync(readDb, CancellationToken.None));
        var sample = Assert.Single(group.Samples);

        Assert.Equal("Calibration", sample.JobType);
        Assert.Equal(2, sample.VerificationChecks.Count);
        Assert.All(sample.VerificationChecks, check => Assert.Equal(CheckOutcome.Failed.ToString(), check.Outcome));
        Assert.Contains(sample.VerificationChecks, check =>
            check.Name == "Duration" && check.Detail.Contains("17.4s", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ListAsync_surfaces_the_stored_failure_category()
    {
        await using (var db = new OptimisarrDbContext(_options))
        {
            var library = new Library { Name = "Films", Path = "/data/films" };
            db.Libraries.Add(library);
            await db.SaveChangesAsync();
            db.MediaFiles.Add(MediaFile(library.Id, id: 1));
            await db.SaveChangesAsync();
            var job = Failed(id: 1, "Could not find tag for codec none");
            job.FailureCategory = FailureCategory.ContainerIncompatibility;
            db.Jobs.Add(job);
            await db.SaveChangesAsync();
        }

        await using var readDb = new OptimisarrDbContext(_options);
        var listed = Assert.Single(await JobQueries.ListAsync(readDb, CancellationToken.None));

        Assert.Equal("ContainerIncompatibility", listed.FailureCategory);
    }

    [Fact]
    public async Task QueryAsync_filters_by_library_category_and_pages_with_a_total()
    {
        await using (var db = new OptimisarrDbContext(_options))
        {
            var a = new Library { Name = "A", Path = "/data/a" };
            var b = new Library { Name = "B", Path = "/data/b" };
            db.Libraries.AddRange(a, b);
            await db.SaveChangesAsync();
            for (var id = 1; id <= 5; id++)
            {
                db.MediaFiles.Add(MediaFile(id <= 4 ? a.Id : b.Id, id));
            }
            await db.SaveChangesAsync();

            // Library A: 3 size-saving failures + 1 container failure. Library B: 1 size-saving.
            db.Jobs.Add(FailedIn(a.Id, id: 1, FailureCategory.SizeSaving));
            db.Jobs.Add(FailedIn(a.Id, id: 2, FailureCategory.SizeSaving));
            db.Jobs.Add(FailedIn(a.Id, id: 3, FailureCategory.SizeSaving));
            db.Jobs.Add(FailedIn(a.Id, id: 4, FailureCategory.ContainerIncompatibility));
            db.Jobs.Add(FailedIn(b.Id, id: 5, FailureCategory.SizeSaving));
            await db.SaveChangesAsync();

            await using var readDb = new OptimisarrDbContext(_options);
            // Library A + size-saving = 3 matches; page size 2 returns the first 2 but reports total 3.
            var result = await JobQueries.QueryAsync(readDb, new JobQuery
            {
                LibraryId = a.Id,
                Category = FailureCategory.SizeSaving,
                PageSize = 2,
                Page = 1
            }, CancellationToken.None);

            Assert.Equal(3, result.Total);
            Assert.Equal(2, result.Items.Count);
            Assert.All(result.Items, job => Assert.Equal(a.Id, job.LibraryId));

            var page2 = await JobQueries.QueryAsync(readDb, new JobQuery
            {
                LibraryId = a.Id,
                Category = FailureCategory.SizeSaving,
                PageSize = 2,
                Page = 2
            }, CancellationToken.None);
            Assert.Single(page2.Items);
        }
    }

    [Fact]
    public async Task QueryAsync_filters_by_a_finished_date_range()
    {
        var cutoff = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        await using (var db = new OptimisarrDbContext(_options))
        {
            var library = new Library { Name = "Films", Path = "/data/films" };
            db.Libraries.Add(library);
            await db.SaveChangesAsync();
            db.MediaFiles.Add(MediaFile(library.Id, 1));
            db.MediaFiles.Add(MediaFile(library.Id, 2));
            await db.SaveChangesAsync();

            var old = Failed(id: 1, "Verification failed: Size saving");
            old.FinishedAt = cutoff.AddDays(-2);
            var recent = Failed(id: 2, "Verification failed: Size saving");
            recent.FinishedAt = cutoff.AddDays(2);
            db.Jobs.AddRange(old, recent);
            await db.SaveChangesAsync();
        }

        await using var readDb = new OptimisarrDbContext(_options);
        var result = await JobQueries.QueryAsync(
            readDb, new JobQuery { Since = cutoff }, CancellationToken.None);

        Assert.Equal(2, Assert.Single(result.Items).Id);   // only the job finished after the cutoff
        Assert.Equal(1, result.Total);
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

    // The dashboard needs the handful of jobs that are actually in progress. Without a filter it
    // had to fetch every job and sift client-side: on a real library that is the entire history —
    // 1,773 rows on the machine this was written against — re-downloaded on every refresh.
    [Fact]
    public async Task QueryAsync_live_returns_only_jobs_still_in_progress()
    {
        await using (var db = new OptimisarrDbContext(_options))
        {
            var library = new Library { Name = "Films", Path = "/data/films" };
            db.Libraries.Add(library);
            await db.SaveChangesAsync();
            for (var id = 1; id <= 7; id++) db.MediaFiles.Add(MediaFile(library.Id, id));
            await db.SaveChangesAsync();

            db.Jobs.Add(WithStatus(1, JobStatus.Transcoding));
            db.Jobs.Add(WithStatus(2, JobStatus.Verifying));
            db.Jobs.Add(WithStatus(3, JobStatus.Leased));
            db.Jobs.Add(WithStatus(4, JobStatus.AwaitingVerification));
            db.Jobs.Add(WithStatus(5, JobStatus.Probing));
            // Terminal and not-yet-started states are not "in progress".
            db.Jobs.Add(WithStatus(6, JobStatus.Completed));
            db.Jobs.Add(WithStatus(7, JobStatus.Queued));
            await db.SaveChangesAsync();
        }

        await using var readDb = new OptimisarrDbContext(_options);
        var result = await JobQueries.QueryAsync(readDb, new JobQuery { Live = true }, CancellationToken.None);

        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, result.Items.Select(job => job.Id).OrderBy(id => id));
        Assert.Equal(5, result.Total);
    }

    [Fact]
    public async Task QueryAsync_live_still_honours_paging_and_the_library_filter()
    {
        await using (var db = new OptimisarrDbContext(_options))
        {
            var films = new Library { Name = "Films", Path = "/data/films" };
            var tv = new Library { Name = "TV", Path = "/data/tv" };
            db.Libraries.AddRange(films, tv);
            await db.SaveChangesAsync();
            db.MediaFiles.Add(MediaFile(films.Id, id: 1));
            db.MediaFiles.Add(MediaFile(films.Id, id: 2));
            db.MediaFiles.Add(MediaFile(tv.Id, id: 3));
            await db.SaveChangesAsync();
            db.Jobs.Add(WithStatus(1, JobStatus.Transcoding, films.Id));
            db.Jobs.Add(WithStatus(2, JobStatus.Verifying, films.Id));
            db.Jobs.Add(WithStatus(3, JobStatus.Transcoding, tv.Id));
            await db.SaveChangesAsync();

            var filmsId = films.Id;
            await using var readDb = new OptimisarrDbContext(_options);
            var scoped = await JobQueries.QueryAsync(
                readDb, new JobQuery { Live = true, LibraryId = filmsId }, CancellationToken.None);
            Assert.Equal(2, scoped.Total);

            var firstPage = await JobQueries.QueryAsync(
                readDb, new JobQuery { Live = true, PageSize = 1 }, CancellationToken.None);
            Assert.Single(firstPage.Items);
            Assert.Equal(3, firstPage.Total);
        }
    }

    private static Job WithStatus(int id, JobStatus status, int? libraryId = null) => new()
    {
        Id = id,
        MediaFileId = id,
        LibraryId = libraryId,
        Priority = 1,
        Status = status,
        EnqueuedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(id)
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

    private static Job FailedIn(int libraryId, int id, FailureCategory category) => new()
    {
        Id = id,
        MediaFileId = id,
        LibraryId = libraryId,
        Priority = 1,
        Status = JobStatus.Failed,
        FailureCategory = category,
        EnqueuedAt = DateTimeOffset.UtcNow,
        FinishedAt = DateTimeOffset.UtcNow
    };

    public void Dispose() => _connection.Dispose();
}
