using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Diagnostics;
using Optimisarr.Api.Queue;
using Optimisarr.Core.Verification;
using Optimisarr.Core.Workers;
using Optimisarr.Data;

namespace Optimisarr.Tests;

public sealed class DiagnosticCaptureStoreTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private DbContextOptions<OptimisarrDbContext> _options = null!;
    private readonly DateTimeOffset _now = new(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _options = new DbContextOptionsBuilder<OptimisarrDbContext>().UseSqlite(_connection).Options;
        await using var db = Db();
        await db.Database.MigrateAsync();
        await db.Database.MigrateAsync(); // An upgraded database must not change on a second startup.
    }

    [Fact]
    public async Task No_enhanced_event_is_persisted_before_explicit_opt_in()
    {
        await using var db = Db();
        var job = new Job { Id = 42, Status = JobStatus.Transcoding, ExecutionAttempt = 1 };

        Assert.False(await DiagnosticEventCapture.AppendJobTransitionAsync(
            db, job, JobStatus.Queued, _now, CancellationToken.None));
        Assert.Empty(await db.DiagnosticEvents.ToListAsync());
    }

    [Fact]
    public async Task Scoped_session_records_only_its_job_until_expiry_then_stops()
    {
        Guid sessionId;
        await using (var db = Db())
        {
            var session = await new DiagnosticCaptureStore(db).StartAsync(
                durationHours: 1, scopedJobId: 42, includePaths: false,
                nowUtc: _now, cancellationToken: CancellationToken.None);
            sessionId = session.Id;
        }

        await using (var db = Db())
        {
            var other = new Job { Id = 43, Status = JobStatus.Transcoding, ExecutionAttempt = 1 };
            var wanted = new Job { Id = 42, Status = JobStatus.Verifying, ExecutionAttempt = 2 };
            Assert.False(await DiagnosticEventCapture.AppendJobTransitionAsync(
                db, other, JobStatus.Queued, _now.AddMinutes(1), CancellationToken.None));
            Assert.True(await DiagnosticEventCapture.AppendJobTransitionAsync(
                db, wanted, JobStatus.Transcoding, _now.AddMinutes(2), CancellationToken.None));
            await db.SaveChangesAsync();
        }

        await using (var db = Db())
        {
            var events = await db.DiagnosticEvents.ToListAsync();
            Assert.Single(events);
            Assert.Equal(sessionId, events[0].SessionId);
            Assert.Equal("Transcoding", events[0].PreviousStatus);
            Assert.Equal("Verifying", events[0].CurrentStatus);
            Assert.Equal(2, events[0].Attempt);
            Assert.False(await DiagnosticEventCapture.AppendJobTransitionAsync(
                db, new Job { Id = 42, Status = JobStatus.Failed }, JobStatus.Verifying,
                _now.AddHours(1), CancellationToken.None));
        }
    }

    [Fact]
    public async Task Stopping_capture_persists_without_rearming_after_restart()
    {
        Guid sessionId;
        await using (var db = Db())
        {
            sessionId = (await new DiagnosticCaptureStore(db).StartAsync(
                null, null, false, _now, CancellationToken.None)).Id;
        }
        await using (var db = Db())
        {
            await new DiagnosticCaptureStore(db).StopAsync(sessionId, _now.AddMinutes(3), CancellationToken.None);
        }
        await using (var db = Db())
        {
            Assert.Null(await new DiagnosticCaptureStore(db).GetActiveAsync(
                _now.AddMinutes(4), CancellationToken.None));
            Assert.False(await DiagnosticEventCapture.AppendJobTransitionAsync(
                db, new Job { Id = 9, Status = JobStatus.Failed }, JobStatus.Verifying,
                _now.AddMinutes(4), CancellationToken.None));
        }
    }

    [Fact]
    public async Task Cleanup_removes_ended_routine_capture_after_seven_days()
    {
        Guid sessionId;
        await using (var db = Db())
        {
            var store = new DiagnosticCaptureStore(db);
            sessionId = (await store.StartAsync(1, null, false, _now, CancellationToken.None)).Id;
            db.DiagnosticEvents.Add(new DiagnosticEvent
            {
                SessionId = sessionId, JobId = 42, OccurredAt = _now,
                ReasonCode = "Job.StatusChanged", CurrentStatus = "Verifying"
            });
            await db.SaveChangesAsync();
        }

        await using (var db = Db())
        {
            var store = new DiagnosticCaptureStore(db);
            Assert.Equal(0, await store.PruneEndedAsync(_now.AddDays(7), CancellationToken.None));
            Assert.Equal(1, await store.PruneEndedAsync(_now.AddDays(8), CancellationToken.None));
            Assert.Null(await store.GetAsync(sessionId, CancellationToken.None));
            Assert.Empty(await db.DiagnosticEvents.ToListAsync());
        }
    }

    [Fact]
    public async Task Cleanup_keeps_failure_evidence_thirty_days_and_never_removes_active_session()
    {
        Guid sessionId;
        await using (var db = Db())
        {
            var store = new DiagnosticCaptureStore(db);
            sessionId = (await store.StartAsync(1, null, false, _now, CancellationToken.None)).Id;
            db.DiagnosticEvents.Add(new DiagnosticEvent
            {
                SessionId = sessionId, JobId = 42, OccurredAt = _now,
                ReasonCode = "Failure.DecodeError", CurrentStatus = "Failed"
            });
            await db.SaveChangesAsync();
        }

        await using (var db = Db())
        {
            var store = new DiagnosticCaptureStore(db);
            Assert.Equal(0, await store.PruneEndedAsync(_now.AddDays(29), CancellationToken.None));
            Assert.Equal(1, await store.PruneEndedAsync(_now.AddDays(31), CancellationToken.None));
            await store.StartAsync(null, null, false, _now.AddDays(31), CancellationToken.None);
            Assert.Equal(0, await store.PruneEndedAsync(_now.AddYears(1), CancellationToken.None));
            Assert.Single(await db.DiagnosticCaptureSessions.ToListAsync());
        }
    }

    [Fact]
    public async Task Job_status_change_and_event_commit_together_without_caller_instrumentation()
    {
        int jobId;
        await using (var db = Db())
        {
            var media = new MediaFile { Path = "/data/private-title.mkv", RelativePath = "private-title.mkv" };
            db.MediaFiles.Add(media);
            await db.SaveChangesAsync();
            var job = new Job { MediaFileId = media.Id, Status = JobStatus.Queued };
            db.Jobs.Add(job);
            await db.SaveChangesAsync();
            jobId = job.Id;
            await new DiagnosticCaptureStore(db).StartAsync(1, jobId, false,
                DateTimeOffset.UtcNow, CancellationToken.None);
        }

        await using (var db = Db())
        {
            var job = await db.Jobs.SingleAsync(candidate => candidate.Id == jobId);
            job.Status = JobStatus.Verifying;
            job.ExecutionAttempt = 3;
            await db.SaveChangesAsync();
        }

        await using (var db = Db())
        {
            var entry = await db.DiagnosticEvents.SingleAsync();
            Assert.Equal(jobId, entry.JobId);
            Assert.Equal(3, entry.Attempt);
            Assert.Equal("Queued", entry.PreviousStatus);
            Assert.Equal("Verifying", entry.CurrentStatus);
            Assert.DoesNotContain("private-title", System.Text.Json.JsonSerializer.Serialize(entry));
        }
    }

    [Fact]
    public async Task Worker_claim_event_keeps_the_lease_and_worker_correlation()
    {
        int jobId;
        await using (var db = Db())
        {
            var media = new MediaFile { Path = "/data/claim.mkv", RelativePath = "claim.mkv" };
            db.MediaFiles.Add(media);
            await db.SaveChangesAsync();
            var job = new Job { MediaFileId = media.Id, Status = JobStatus.Queued };
            db.Jobs.Add(job);
            await db.SaveChangesAsync();
            jobId = job.Id;
            await new DiagnosticCaptureStore(db).StartAsync(1, jobId, false,
                DateTimeOffset.UtcNow, CancellationToken.None);
        }

        var leaseId = Guid.NewGuid();
        int workerId;
        await using (var db = Db())
        {
            var worker = new Worker { Name = "PICARD", OperatingSystem = "windows", Architecture = "x64" };
            db.Workers.Add(worker);
            await db.SaveChangesAsync();
            workerId = worker.Id;
            var job = await db.Jobs.SingleAsync(candidate => candidate.Id == jobId);
            db.JobLeases.Add(new JobLease
            {
                Id = leaseId, JobId = jobId, WorkerId = workerId,
                AcquiredAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
            });
            job.Status = JobStatus.Leased;
            job.ExecutionAttempt = 1;
            await db.SaveChangesAsync();
        }

        await using (var db = Db())
        {
            var entry = await db.DiagnosticEvents.SingleAsync();
            Assert.Equal(leaseId, entry.LeaseId);
            Assert.Equal(workerId, entry.WorkerId);
            Assert.Equal("Leased", entry.CurrentStatus);
        }
    }

    [Fact]
    public async Task Capture_stops_appending_events_at_the_disclosed_limit_without_blocking_job_changes()
    {
        int jobId;
        Guid sessionId;
        await using (var db = Db())
        {
            var media = new MediaFile { Path = "/data/limit.mkv", RelativePath = "limit.mkv" };
            db.MediaFiles.Add(media);
            await db.SaveChangesAsync();
            var job = new Job { MediaFileId = media.Id, Status = JobStatus.Queued };
            db.Jobs.Add(job);
            await db.SaveChangesAsync();
            jobId = job.Id;
            var session = await new DiagnosticCaptureStore(db).StartAsync(
                1, jobId, false, DateTimeOffset.UtcNow, CancellationToken.None);
            sessionId = session.Id;
            session.EventsStored = 9_999;
            await db.SaveChangesAsync();
        }

        await using (var db = Db())
        {
            var job = await db.Jobs.SingleAsync(candidate => candidate.Id == jobId);
            job.Status = JobStatus.Probing;
            await db.SaveChangesAsync();
            job.Status = JobStatus.Transcoding;
            await db.SaveChangesAsync();
        }

        await using (var db = Db())
        {
            var session = await db.DiagnosticCaptureSessions.SingleAsync(candidate => candidate.Id == sessionId);
            Assert.Equal(10_000, session.EventsStored);
            Assert.True(session.EventLimitReached);
            Assert.Single(await db.DiagnosticEvents.ToListAsync());
            Assert.Equal(JobStatus.Transcoding,
                (await db.Jobs.SingleAsync(candidate => candidate.Id == jobId)).Status);
        }
    }

    [Fact]
    public async Task Job_bundle_uses_structured_fields_and_excludes_paths_and_secrets_by_default()
    {
        Guid sessionId;
        int jobId;
        await using (var db = Db())
        {
            var media = new MediaFile
            {
                Path = "/data/private-title/secret-token.mkv",
                RelativePath = "private-title/secret-token.mkv"
            };
            db.MediaFiles.Add(media);
            await db.SaveChangesAsync();
            var report = new VerificationReport([
                new VerificationCheck("Decode health", CheckOutcome.Failed,
                    "Authorization: Bearer secret-token"),
                .. Enumerable.Repeat(new VerificationCheck("Output readable", CheckOutcome.Passed,
                    "Authorization: Bearer secret-token"), 120)
            ]);
            var job = new Job
            {
                MediaFileId = media.Id,
                Status = JobStatus.Failed,
                ExecutionAttempt = 2,
                SourceSha256 = new string('a', 64),
                VerificationReportJson = System.Text.Json.JsonSerializer.Serialize(report),
                ProcessLog = "Authorization: Bearer secret-token",
                FfmpegArguments = "-i /data/private-title/secret-token.mkv",
                AttemptHistoryJson = System.Text.Json.JsonSerializer.Serialize(new[]
                {
                    new JobAttemptSnapshot(1, "Bearer secret-token", "Bearer secret-token", null,
                        null, DateTimeOffset.UtcNow, false, System.Text.Json.JsonSerializer.Serialize(report),
                        DateTimeOffset.UtcNow, null, "Bearer secret-token", "Bearer secret-token",
                        "Bearer secret-token", "Bearer secret-token")
                })
            };
            db.Jobs.Add(job);
            await db.SaveChangesAsync();
            jobId = job.Id;
            sessionId = (await new DiagnosticCaptureStore(db).StartAsync(
                1, jobId, false, DateTimeOffset.UtcNow, CancellationToken.None)).Id;
            var worker = new Worker
            {
                Name = "Bearer secret-token",
                OperatingSystem = "Bearer secret-token",
                SidecarVersion = "Bearer secret-token",
                Architecture = "x64",
                CredentialFingerprint = "secret-token"
            };
            db.Workers.Add(worker);
            await db.SaveChangesAsync();
            db.JobLeases.Add(new JobLease
            {
                Id = Guid.NewGuid(), JobId = jobId, WorkerId = worker.Id,
                AcquiredAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
                State = LeaseState.Completed,
                DeliveredSha256 = "secret-token",
                VerificationEvidenceJson = "Bearer secret-token"
            });
            db.DiagnosticEvents.Add(new DiagnosticEvent
            {
                SessionId = sessionId, JobId = jobId, OccurredAt = DateTimeOffset.UtcNow,
                ReasonCode = "Bearer secret-token", CurrentStatus = "Bearer secret-token"
            });
            await db.SaveChangesAsync();
        }

        await using (var db = Db())
        {
            var bundle = await DiagnosticJobBundleQueries.BuildAsync(db, sessionId, jobId,
                DateTimeOffset.UtcNow, CancellationToken.None);
            var json = System.Text.Json.JsonSerializer.Serialize(bundle);
            Assert.Equal(100, bundle.Job.CurrentReport?.Checks.Count);
            Assert.Contains("Decode health", json);
            Assert.Contains(new string('a', 64), json);
            Assert.DoesNotContain("private-title", json);
            Assert.DoesNotContain("secret-token", json);
            Assert.DoesNotContain("Authorization", json);
            Assert.Contains("sidecar", json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Bundle_distinguishes_rejected_mac_attempt_from_current_picard_candidate()
    {
        Guid sessionId;
        int jobId;
        await using (var db = Db())
        {
            var media = new MediaFile { Path = "/data/title.mkv", RelativePath = "title.mkv" };
            db.MediaFiles.Add(media);
            await db.SaveChangesAsync();
            var rejected = new JobAttemptSnapshot(
                1, "Mac", "hevc_videotoolbox", "videotoolbox", _now,
                _now.AddMinutes(30), false, null, _now.AddMinutes(30), 400,
                "Rejected", "HardwareDecodeCorruption", null, null);
            var job = new Job
            {
                MediaFileId = media.Id, Status = JobStatus.AwaitingVerification,
                ExecutionAttempt = 2, VideoEncoder = "hevc_nvenc",
                AttemptHistoryJson = System.Text.Json.JsonSerializer.Serialize(new[] { rejected })
            };
            db.Jobs.Add(job);
            var mac = new Worker { Name = "Mac", OperatingSystem = "macos", Architecture = "arm64" };
            var picard = new Worker { Name = "PICARD", OperatingSystem = "windows", Architecture = "x64" };
            db.Workers.AddRange(mac, picard);
            await db.SaveChangesAsync();
            jobId = job.Id;
            db.JobLeases.AddRange(
                new JobLease
                {
                    Id = Guid.NewGuid(), JobId = jobId, WorkerId = mac.Id,
                    AcquiredAt = _now, EndedAt = _now.AddMinutes(30),
                    ExpiresAt = _now.AddMinutes(35), State = LeaseState.Completed
                },
                new JobLease
                {
                    Id = Guid.NewGuid(), JobId = jobId, WorkerId = picard.Id,
                    AcquiredAt = _now.AddMinutes(31), EndedAt = _now.AddMinutes(60),
                    ExpiresAt = _now.AddMinutes(65), State = LeaseState.Completed
                });
            await db.SaveChangesAsync();
            sessionId = (await new DiagnosticCaptureStore(db).StartAsync(
                1, jobId, false, _now, CancellationToken.None)).Id;
        }

        await using (var db = Db())
        {
            var bundle = await DiagnosticJobBundleQueries.BuildAsync(
                db, sessionId, jobId, _now.AddMinutes(40), CancellationToken.None);
            Assert.Equal(2, bundle.Job.Attempt);
            Assert.Equal("AwaitingVerification", bundle.Job.Status);
            Assert.Equal("hevc_nvenc", bundle.Job.VideoEncoder);
            var old = Assert.Single(bundle.Attempts);
            Assert.Equal(1, old.Number);
            Assert.Equal("hevc_videotoolbox", old.Encoder);
            Assert.Equal("videotoolbox", old.HardwareDecoder);
            Assert.Equal("HardwareDecodeCorruption", old.ReasonCode);
            Assert.Equal(bundle.Leases[0].WorkerId, old.WorkerId);
            Assert.NotEqual(bundle.Leases[1].WorkerId, old.WorkerId);
        }
    }

    private OptimisarrDbContext Db() => new(_options);

    public async Task DisposeAsync() => await _connection.DisposeAsync();
}
