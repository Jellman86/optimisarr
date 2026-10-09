using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Diagnostics;
using Optimisarr.Api.Queue;
using Optimisarr.Core.Verification;
using Optimisarr.Core.Diagnostics;
using Optimisarr.Core.Workers;
using Optimisarr.Data;

namespace Optimisarr.Tests;

public sealed class CorrelatedDiagnosticsTests : IAsyncLifetime
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private OptimisarrDbContext Db() => new(new DbContextOptionsBuilder<OptimisarrDbContext>().UseSqlite(connection).Options);
    public async Task InitializeAsync() { await connection.OpenAsync(); await using var db = Db(); await db.Database.MigrateAsync(); await db.Database.MigrateAsync(); }
    public async Task DisposeAsync() => await connection.DisposeAsync();

    [Fact]
    public async Task Restart_stops_capture_unless_persistence_was_explicitly_selected()
    {
        await using var db = Db();
        var store = new DiagnosticCaptureStore(db);
        var now = DateTimeOffset.UtcNow;
        var first = await store.StartAsync(1, null, false, now, default);
        await store.StopOnRestartAsync(now.AddMinutes(1), default);
        Assert.Null(await store.GetActiveAsync(now.AddMinutes(2), default));
        var second = await store.StartAsync(1, null, false, now.AddMinutes(2), default,
            new DiagnosticCaptureOptions(PersistAcrossRestart: true));
        await store.StopOnRestartAsync(now.AddMinutes(3), default);
        Assert.Equal(second.Id, (await store.GetActiveAsync(now.AddMinutes(4), default))?.Id);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public async Task Sidecar_replays_are_idempotent_owned_by_worker_and_never_cross_job_scope()
    {
        await using var db = Db();
        var worker = new Worker { Name = "test", OperatingSystem = "macos", Architecture = "arm64" };
        var media = new MediaFile { Path = "/synthetic/test.mkv", RelativePath = "test.mkv" }; db.MediaFiles.Add(media); await db.SaveChangesAsync();
        db.Workers.Add(worker); var job = new Job { MediaFileId = media.Id }; var other = new Job { MediaFileId = media.Id, Status = JobStatus.Failed }; db.Jobs.AddRange(job, other); await db.SaveChangesAsync();
        var lease = new JobLease { Id = Guid.NewGuid(), WorkerId = worker.Id, JobId = job.Id, AcquiredAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2) };
        db.JobLeases.Add(lease); await db.SaveChangesAsync();
        var now = DateTimeOffset.UtcNow;
        var session = await new DiagnosticCaptureStore(db).StartAsync(1, job.Id, false, now, default);
        var batch = new SidecarDiagnosticBatch(1, session.Id, Guid.NewGuid(), [new(1, now, lease.Id, job.Id, "Worker.StageChanged", "Encoding", 2.5)]);
        var store = new SidecarDiagnosticStore(db);
        Assert.Equal(1, await store.AppendAsync(worker.Id, batch, now.AddSeconds(1), default));
        Assert.Equal(1, await store.AppendAsync(worker.Id, batch, now.AddSeconds(2), default));
        Assert.Single(await db.DiagnosticEvents.Where(e => e.Source == "Sidecar").ToListAsync());
        await Assert.ThrowsAsync<ArgumentException>(() => store.AppendAsync(worker.Id + 1, batch, now.AddSeconds(2), default));
        await Assert.ThrowsAsync<ArgumentException>(() => store.AppendAsync(worker.Id, batch with { Events = [batch.Events[0] with { JobId = other.Id }] }, now.AddSeconds(2), default));
        await Assert.ThrowsAsync<ArgumentException>(() => store.AppendAsync(worker.Id, batch with { SchemaVersion = 999 }, now.AddSeconds(2), default));
        Assert.Equal(0, await store.AppendAsync(worker.Id, batch, now.AddHours(2), default));
    }

    [Fact]
    public async Task Attempt_identity_is_frozen_and_replacement_intent_and_outcome_survive_reopening()
    {
        int jobId; Guid captureId;
        await using (var db = Db())
        {
            var now = DateTimeOffset.UtcNow;
            captureId = (await new DiagnosticCaptureStore(db).StartAsync(1, null, false, now, default)).Id;
            var worker = new Worker { Name = "secret-token", SidecarVersion = "0.2.22", OperatingSystem = "macos", Architecture = "arm64", VideoEncoders = "hevc_videotoolbox,secret-token" };
            var media = new MediaFile { Path = "/synthetic/identity.mkv", RelativePath = "identity.mkv" }; db.MediaFiles.Add(media); await db.SaveChangesAsync();
            var job = new Job { MediaFileId = media.Id, VideoEncoder = "hevc_videotoolbox", ExecutionAttempt = 1 };
            db.Workers.Add(worker); db.Jobs.Add(job); await db.SaveChangesAsync(); jobId = job.Id;
            db.JobLeases.Add(new() { Id = Guid.NewGuid(), JobId = job.Id, WorkerId = worker.Id, AcquiredAt = now, ExpiresAt = now.AddHours(1) });
            job.Status = JobStatus.Leased; await db.SaveChangesAsync();
            worker.SidecarVersion = "9.9.9"; worker.OperatingSystem = "windows"; await db.SaveChangesAsync();
            var replacement = new Replacement { JobId = job.Id, MediaFileId = media.Id, Status = ReplacementStatus.Pending };
            db.Replacements.Add(replacement); await db.SaveChangesAsync();
            replacement.Status = ReplacementStatus.Replaced; await db.SaveChangesAsync();
        }
        await using (var db = Db())
        {
            var events = await db.DiagnosticEvents.Where(e => e.JobId == jobId).OrderBy(e => e.Id).ToListAsync();
            var json = JsonSerializer.Serialize(events);
            Assert.Contains("macos", json); Assert.Contains("0.2.22", json);
            Assert.DoesNotContain("secret-token", json);
            Assert.Contains(events, e => e.ReasonCode == "Replacement.Pending");
            Assert.Contains(events, e => e.ReasonCode == "Replacement.Replaced");
            Assert.All(events, e => Assert.Equal(captureId, e.SessionId));
        }
    }

    [Fact]
    public async Task Retention_respects_pin_and_failure_window_and_storage_cap_stops_capture()
    {
        await using var db = Db(); var store = new DiagnosticCaptureStore(db); var now = DateTimeOffset.UtcNow;
        var session = await store.StartAsync(1, null, false, now, default, new DiagnosticCaptureOptions(RetentionDays: 1, FailureRetentionDays: 2, MaximumBytes: 65536));
        session.BytesStored = session.MaximumBytes; await db.SaveChangesAsync();
        Assert.False(await DiagnosticEventCapture.AppendJobTransitionAsync(db, new Job { Status = JobStatus.Failed }, JobStatus.Queued, now.AddSeconds(1), default));
        await db.SaveChangesAsync(); Assert.True(session.EventLimitReached);
        await store.StopAsync(session.Id, now, default); await store.SetPinnedAsync(session.Id, true, default);
        Assert.Equal(0, await store.PruneEndedAsync(now.AddDays(3), default));
        await store.SetPinnedAsync(session.Id, false, default);
        Assert.Equal(1, await store.PruneEndedAsync(now.AddDays(3), default));
    }
    [Fact]
    public async Task Cross_worker_software_decode_retry_retains_both_verdicts_after_restart()
    {
        Guid sessionId; int jobId, macId, picardId;
        await using (var db = Db())
        {
            var now = DateTimeOffset.UtcNow;
            sessionId = (await new DiagnosticCaptureStore(db).StartAsync(1, null, false, now, default, new(PersistAcrossRestart: true))).Id;
            var media = new MediaFile { Path = "/synthetic/retry.mkv", RelativePath = "retry.mkv" };
            var mac = new Worker { OperatingSystem = "macos", Architecture = "arm64", SidecarVersion = "0.2.22", Name = "Mac" };
            var picard = new Worker { OperatingSystem = "windows", Architecture = "x64", SidecarVersion = "0.2.22", Name = "PICARD" };
            db.AddRange(media, mac, picard); await db.SaveChangesAsync(); macId = mac.Id; picardId = picard.Id;
            var job = new Job { MediaFileId = media.Id, ExecutionAttempt = 1, VideoEncoder = "hevc_videotoolbox", Status = JobStatus.Leased };
            db.Jobs.Add(job); await db.SaveChangesAsync(); jobId = job.Id;
            var lease = new JobLease { Id = Guid.NewGuid(), JobId = job.Id, WorkerId = mac.Id, AcquiredAt = now, ExpiresAt = now.AddHours(1), HardwareDecoder = "videotoolbox" };
            db.JobLeases.Add(lease); await db.SaveChangesAsync();
            job.VerificationReportJson = JsonSerializer.Serialize(new VerificationReport([new("Timestamp integrity", CheckOutcome.Failed, "private process text")], new("hevc_videotoolbox", 24, 24, "cq", 0, null, 90, 80, 60, VerificationLocation: "Worker")));
            job.VerificationPassed = false;
            // The real retry clears the rejected report before its single SaveChanges call.
            JobAttemptHistory.RequeueAfterRejectedCandidate(job, mac.Name, "videotoolbox", now.AddSeconds(1));
            lease.State = LeaseState.Released; await db.SaveChangesAsync();
            job.ExecutionAttempt = 2; job.VideoEncoder = "hevc_nvenc"; job.Status = JobStatus.Leased;
            var next = new JobLease { Id = Guid.NewGuid(), JobId = job.Id, WorkerId = picard.Id, AcquiredAt = now.AddSeconds(2), ExpiresAt = now.AddHours(1) };
            db.JobLeases.Add(next); await db.SaveChangesAsync();
            job.VerificationReportJson = JsonSerializer.Serialize(new VerificationReport([new("Timestamp integrity", CheckOutcome.Passed, "private process text")], new("hevc_nvenc", 24, 24, "cq", 0, null, 90, 80, 60, VerificationLocation: "Worker")));
            job.VerificationPassed = true; job.Status = JobStatus.ReadyToReplace; await db.SaveChangesAsync();
        }
        await using (var db = Db())
        {
            await new DiagnosticCaptureStore(db).StopOnRestartAsync(DateTimeOffset.UtcNow, default);
            var bundle = await DiagnosticJobBundleQueries.BuildAsync(db, sessionId, jobId, DateTimeOffset.UtcNow, default);
            var rejected = Assert.Single(bundle.Events, e => e.ReasonCode == "Attempt.Archived");
            Assert.Equal(1, rejected.Attempt); Assert.Equal(macId, rejected.WorkerId);
            Assert.False(rejected.Details!.Value.GetProperty("report").GetProperty("passed").GetBoolean());
            Assert.Equal("hevc_videotoolbox", rejected.Details.Value.GetProperty("encoder").GetString());
            Assert.Contains(bundle.Events, e => e.ReasonCode == "Retry.SoftwareDecode");
            Assert.Contains(bundle.Events, e => e.Attempt == 2 && e.WorkerId == picardId && e.Details is { } d && d.TryGetProperty("report", out var r) && r.GetProperty("passed").GetBoolean());
            Assert.DoesNotContain("private process text", JsonSerializer.Serialize(bundle));
        }
    }
    [Fact]
    public async Task Scheduler_decisions_are_opt_in_scoped_and_deduplicated()
    {
        await using var db = Db(); var capture = new DiagnosticSchedulingCapture(); var now = DateTimeOffset.UtcNow;
        var snapshot = DiagnosticSchedulingSnapshot.Create(true, false, false, 3, 0, 0, 0, true, true);
        await capture.RecordAsync(db, snapshot, now, default); Assert.Empty(await db.DiagnosticEvents.ToListAsync());
        var store = new DiagnosticCaptureStore(db); var session = await store.StartAsync(1, 42, false, now, default);
        await capture.RecordAsync(db, snapshot, now.AddSeconds(1), default);
        await capture.RecordAsync(db, snapshot, now.AddSeconds(2), default);
        var decision = Assert.Single(await db.DiagnosticEvents.ToListAsync());
        Assert.Equal(42, decision.JobId); Assert.Equal("Queue.DispatchDecision", decision.ReasonCode);
        Assert.Contains("LibraryWindow", decision.DetailsJson);
        await store.StopAsync(session.Id, now.AddSeconds(3), default);
        await capture.RecordAsync(db, snapshot with { Queued = 4 }, now.AddSeconds(4), default);
        Assert.Single(await db.DiagnosticEvents.ToListAsync());
    }

    [Fact]
    public async Task A_previously_tracked_session_cannot_bypass_another_contexts_storage_cap()
    {
        await using var stale = Db(); var now = DateTimeOffset.UtcNow;
        var session = await new DiagnosticCaptureStore(stale).StartAsync(1, null, false, now, default);
        await using (var writer = Db())
        {
            var updated = await writer.DiagnosticCaptureSessions.SingleAsync(); updated.BytesStored = updated.MaximumBytes;
            await writer.SaveChangesAsync();
        }
        Assert.False(await DiagnosticEventCapture.AppendJobTransitionAsync(stale, new Job { Status = JobStatus.Failed }, JobStatus.Queued, now.AddSeconds(1), default));
        await stale.SaveChangesAsync(); Assert.True(session.EventLimitReached);
        Assert.Empty(await stale.DiagnosticEvents.ToListAsync());
    }
    [Fact]
    public async Task Final_receipts_disclose_local_rotation_and_late_uploads_require_another_final_receipt()
    {
        await using var db = Db(); var now = DateTimeOffset.UtcNow;
        var worker = new Worker { Name = "test", OperatingSystem = "linux", ProtocolVersion = 10, PairedAt = now.AddMinutes(-1), LastSeenAt = now };
        db.Workers.Add(worker);
        var media = new MediaFile { Path = "/synthetic/test.mkv", RelativePath = "test.mkv" };
        db.MediaFiles.Add(media); await db.SaveChangesAsync();
        var job = new Job { MediaFileId = media.Id }; db.Jobs.Add(job); await db.SaveChangesAsync();
        var lease = new JobLease { Id = Guid.NewGuid(), WorkerId = worker.Id, JobId = job.Id, AcquiredAt = now, ExpiresAt = now.AddMinutes(2) };
        db.JobLeases.Add(lease); await db.SaveChangesAsync();
        db.Workers.AddRange(Enumerable.Range(0, 105).Select(i => new Worker { Name = "older", OperatingSystem = "linux", PairedAt = now.AddMinutes(-1) }));
        await db.SaveChangesAsync();
        var session = await new DiagnosticCaptureStore(db).StartAsync(1, null, false, now, default);
        await new DiagnosticCaptureStore(db).StopAsync(session.Id, now.AddSeconds(1), default);
        var store = new SidecarDiagnosticStore(db);
        var batch = new SidecarDiagnosticBatch(1, session.Id, Guid.NewGuid(), [], Final: true, DroppedEvents: 5);
        await store.AppendAsync(worker.Id, batch, now.AddSeconds(2), default);
        var participant = (await DiagnosticSessionBundleQueries.ParticipantsAsync(db, session.Id, now, default)).Single(p => p.WorkerId == worker.Id);
        Assert.Equal("CollectedWithLocalOmissions", participant.State); Assert.Equal(5, participant.DroppedEvents);
        await store.AppendAsync(worker.Id, batch with { Final = false, Events = [new(1, now.AddDays(-1), lease.Id, job.Id, "Worker.StageChanged", "Encoding")] }, now.AddSeconds(3), default);
        Assert.Null((await DiagnosticSessionBundleQueries.ParticipantsAsync(db, session.Id, now, default)).Single(p => p.WorkerId == worker.Id).FinalUploadAt);
        var result = await DiagnosticSessionBundleQueries.BuildAsync(db, session.Id, worker.Id, now.AddSeconds(2), now.AddSeconds(4), now, default);
        using var doc = JsonDocument.Parse(result);
        Assert.Single(doc.RootElement.GetProperty("events").EnumerateArray());
        Assert.Equal(worker.Id, doc.RootElement.GetProperty("manifest").GetProperty("participants")[0].GetProperty("workerId").GetInt32());
        Assert.Contains(doc.RootElement.GetProperty("manifest").GetProperty("omissions").EnumerateArray(), e => e.GetString()!.Contains("100"));
        var again = await DiagnosticSessionBundleQueries.BuildAsync(db, session.Id, worker.Id, now.AddSeconds(2), now.AddSeconds(4), now.AddMinutes(1), default);
        using var other = JsonDocument.Parse(again);
        Assert.Equal(doc.RootElement.GetProperty("manifest").GetProperty("manifestId").GetString(), other.RootElement.GetProperty("manifest").GetProperty("manifestId").GetString());
        var empty = await DiagnosticSessionBundleQueries.BuildAsync(db, session.Id, worker.Id, now.AddSeconds(4), null, now, default);
        using var emptyDoc = JsonDocument.Parse(empty); Assert.Empty(emptyDoc.RootElement.GetProperty("events").EnumerateArray());
    }

    [Fact]
    public async Task Custom_failure_retention_outlasts_routine_retention_and_stopping_expired_capture_does_not_extend_it()
    {
        await using var db = Db(); var store = new DiagnosticCaptureStore(db); var now = DateTimeOffset.UtcNow;
        var routine = await store.StartAsync(1, null, false, now, default, new(RetentionDays: 1, FailureRetentionDays: 3));
        await store.StopAsync(routine.Id, now.AddHours(2), default);
        Assert.Equal(now.AddHours(1), (await store.GetAsync(routine.Id, default))!.StoppedAt);
        var failed = await store.StartAsync(1, null, false, now.AddHours(2), default, new(RetentionDays: 1, FailureRetentionDays: 3));
        failed.HasFailure = true; await db.SaveChangesAsync(); await store.StopAsync(failed.Id, now.AddHours(2), default);
        Assert.Equal(1, await store.PruneEndedAsync(now.AddDays(2), default));
        Assert.Equal(failed.Id, (await db.DiagnosticCaptureSessions.SingleAsync()).Id);
        Assert.Equal(1, await store.PruneEndedAsync(now.AddDays(4), default));
    }

    [Fact]
    public async Task Session_download_is_bounded_and_a_narrower_range_recovers_omitted_later_events()
    {
        await using var db = Db(); var now = DateTimeOffset.UtcNow;
        var session = await new DiagnosticCaptureStore(db).StartAsync(1, null, false, now, default, new(MaximumBytes: 16 * 1024 * 1024));
        var report = new DiagnosticVerificationSnapshot(false, Enumerable.Repeat(new DiagnosticGateSnapshot("TimestampIntegrity", "Failed"), 32).ToArray(), 95, 94, 80, 90, 400, "Worker");
        var detail = JsonSerializer.Serialize(new { report }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        db.DiagnosticEvents.AddRange(Enumerable.Range(0, 5000).Select(i => new DiagnosticEvent
        {
            SessionId = session.Id, JobId = 0, ReasonCode = "Job.StatusChanged", CurrentStatus = "Failed",
            OccurredAt = now.AddSeconds(i), ReceivedAt = now.AddSeconds(i), DetailsJson = detail
        }));
        await db.SaveChangesAsync();
        var bytes = await DiagnosticSessionBundleQueries.BuildAsync(db, session.Id, null, null, null, now, default);
        Assert.True(bytes.Length <= DiagnosticSessionBundleQueries.MaximumBundleBytes);
        using var doc = JsonDocument.Parse(bytes);
        Assert.True(doc.RootElement.GetProperty("events").GetArrayLength() < 5000);
        Assert.Contains(doc.RootElement.GetProperty("manifest").GetProperty("omissions").EnumerateArray(), e => e.GetString()!.Contains("Later events omitted"));
        var tail = await DiagnosticSessionBundleQueries.BuildAsync(db, session.Id, null, now.AddSeconds(4999), null, now, default);
        using var tailDoc = JsonDocument.Parse(tail);
        Assert.Single(tailDoc.RootElement.GetProperty("events").EnumerateArray());
    }

    [Fact]
    public async Task Upgrade_preserves_legacy_events_and_backfills_receipt_time_failure_retention_and_storage()
    {
        await using var upgradeConnection = new SqliteConnection("Data Source=:memory:"); await upgradeConnection.OpenAsync();
        await using var db = new OptimisarrDbContext(new DbContextOptionsBuilder<OptimisarrDbContext>().UseSqlite(upgradeConnection).Options);
        var migrator = Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>(db);
        await migrator.MigrateAsync("20261008071607_AddActivityWatcherShowViewerNames");
        var id = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO DiagnosticCaptureSessions (Id, StartedAt, ExpiresAt, IncludePaths, EventsStored, EventLimitReached) VALUES ({id}, {now}, {now.AddHours(1)}, 0, 1, 0)");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO DiagnosticEvents (SessionId, OccurredAt, JobId, Attempt, ReasonCode, CurrentStatus) VALUES ({id}, {now}, 42, 1, 'Failure.DecodeError', 'Failed')");
        await db.Database.MigrateAsync(); await db.Database.MigrateAsync();
        var entry = await db.DiagnosticEvents.SingleAsync(); var session = await db.DiagnosticCaptureSessions.SingleAsync();
        Assert.Equal(now, entry.ReceivedAt); Assert.Equal("Server", entry.Source);
        Assert.True(session.HasFailure); Assert.Equal(512, session.BytesStored);
        Assert.Equal(7, session.RetentionDays); Assert.Equal(30, session.FailureRetentionDays); Assert.False(session.PersistAcrossRestart);
    }

}
