using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Optimisarr.Api.Deduplication;
using Optimisarr.Api.Queue;
using Optimisarr.Api.Workers;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Deduplication;
using Optimisarr.Core.Workers;
using Optimisarr.Data;

namespace Optimisarr.Tests;

public sealed class StartupWorkRecoveryTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "optimisarr-startup-" + Guid.NewGuid());
    private readonly FileOperationClaims _claims = new();
    private readonly ReservationPause _pause = new();
    private ServiceProvider _services = null!;
    private StartupWorkRecovery _recovery = null!;
    private Guid? _reservation;
    private string WorkRoot => Path.Combine(_root, "work");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_root, "library"));
        Directory.CreateDirectory(WorkRoot);
        var services = new ServiceCollection();
        services.AddDbContext<OptimisarrDbContext>(o => o.UseSqlite($"Data Source={Path.Combine(_root, "journal.db")};Pooling=False").AddInterceptors(_pause));
        _services = services.BuildServiceProvider();
        _recovery = NewRecovery();
        await InDb(async db =>
        {
            await db.Database.MigrateAsync();
            db.Libraries.Add(new() { Id = 1, Name = "Fixtures", Path = Path.Combine(_root, "library") });
            db.MediaFiles.AddRange(Enumerable.Range(1, 2).Select(i => new MediaFile { Id = i, LibraryId = 1,
                Path = Path.Combine(_root, "library", $"source-{i}.mkv"), RelativePath = $"source-{i}.mkv" }));
            await db.SaveChangesAsync();
        });
        await File.WriteAllTextAsync(Path.Combine(_root, "library", "source-1.mkv"), "unchanged source");
    }

    [Theory]
    [InlineData("preview")]
    [InlineData("calibration")]
    [InlineData("interrupted")]
    [InlineData("cancelled")]
    [InlineData("orphan")]
    public async Task Startup_preserves_reserved_files_and_rows_then_retries_only_captured_work(string kind)
    {
        var job = kind == "orphan" ? null : await AddJob(kind);
        var output = job?.WorkOutputPath ?? Path.Combine(WorkRoot, "2", "orphan.mkv");
        await WriteOutput(output!);
        Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(output!)!, DateTime.UtcNow.AddDays(-8));
        await Reserve(FileClaimKeys.Media(int.MaxValue));
        Assert.False((await _recovery.RunAsync(default)).Complete);
        Assert.Equal("scratch evidence", await File.ReadAllTextAsync(output!));
        if (job is not null)
        {
            var current = await ReadJob(job.Id);
            Assert.Equal(job.Status, current.Status);
            Assert.Equal(output, current.WorkOutputPath);
            Assert.Equal("diagnostic evidence", current.ErrorMessage);
            Assert.True(_recovery.BlocksDispatch(job.Id));
        }
        await Release();
        Assert.True((await _recovery.RunAsync(default)).Complete);
        Assert.False(File.Exists(output));
        if (job is not null)
        {
            var current = await ReadJob(job.Id);
            Assert.Null(current.WorkOutputPath);
            Assert.Equal(kind == "interrupted" ? JobStatus.Queued : job.Status, current.Status);
            Assert.Equal("diagnostic evidence", current.ErrorMessage);
            Assert.False(_recovery.BlocksDispatch(job.Id));
        }
        Assert.Equal("unchanged source", await File.ReadAllTextAsync(Path.Combine(_root, "library", "source-1.mkv")));
    }

    [Fact]
    public async Task Deferred_startup_does_not_delete_new_preview_or_calibration_sessions()
    {
        var old = await AddJob("preview");
        await WriteOutput(old.WorkOutputPath!);
        await Reserve(FileClaimKeys.Media(int.MaxValue));
        await _recovery.RunAsync(default);
        var preview = await AddJob("preview");
        var calibration = await AddJob("calibration");
        await WriteOutput(preview.WorkOutputPath!);
        await WriteOutput(calibration.WorkOutputPath!);
        await Release();
        Assert.True((await _recovery.RunAsync(default)).Complete);
        Assert.False(File.Exists(old.WorkOutputPath));
        foreach (var fresh in new[] { preview, calibration })
        {
            Assert.True(File.Exists(fresh.WorkOutputPath));
            Assert.Equal(fresh.WorkOutputPath, (await ReadJob(fresh.Id)).WorkOutputPath);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_changed_captured_job_or_source_is_not_recovered_as_old_work(bool changeSource)
    {
        var old = await AddJob("interrupted");
        await WriteOutput(old.WorkOutputPath!);
        await Reserve(FileClaimKeys.Media(int.MaxValue));
        await _recovery.RunAsync(default);
        await InDb(async db =>
        {
            if (changeSource) (await db.MediaFiles.SingleAsync(f => f.Id == 1)).Path = Path.Combine(_root, "library", "new-source.mkv");
            else { var job = await db.Jobs.SingleAsync(j => j.Id == old.Id); job.Status = JobStatus.Queued; job.UpdatedAt = DateTimeOffset.UtcNow; }
            await db.SaveChangesAsync();
        });
        await Release();
        Assert.True((await _recovery.RunAsync(default)).Complete);
        Assert.True(File.Exists(old.WorkOutputPath));
        Assert.Equal(old.WorkOutputPath, (await ReadJob(old.Id)).WorkOutputPath);
    }

    [Fact]
    public async Task Superseded_captured_work_does_not_become_an_orphan_cleanup_target()
    {
        var job = await AddJob("interrupted"); await WriteOutput(job.WorkOutputPath!);
        Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(job.WorkOutputPath!)!, DateTime.UtcNow.AddDays(-8));
        await Reserve(FileClaimKeys.Media(int.MaxValue));
        await _recovery.RunAsync(default);
        await InDb(async db =>
        {
            var current = await db.Jobs.SingleAsync(j => j.Id == job.Id);
            current.Status = JobStatus.Completed;
            current.WorkOutputPath = null;
            current.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        });
        await Release();
        Assert.True((await _recovery.RunAsync(default)).Complete);
        Assert.True(File.Exists(job.WorkOutputPath));
        Assert.Equal(JobStatus.Completed, (await ReadJob(job.Id)).Status);
    }

    [Fact]
    public async Task Process_contention_defers_startup_without_losing_its_retry_plan()
    {
        var job = await AddJob("preview");
        await WriteOutput(job.WorkOutputPath!);
        using (var held = _claims.TryAcquire(["independent operation"]))
        {
            Assert.NotNull(held);
            Assert.False((await _recovery.RunAsync(default)).Complete);
            Assert.True(File.Exists(job.WorkOutputPath));
        }
        Assert.True((await _recovery.RunAsync(default)).Complete);
        Assert.False(File.Exists(job.WorkOutputPath));
    }

    [Fact]
    public async Task New_queued_work_protects_a_previously_orphaned_directory_before_it_has_an_output_reference()
    {
        var output = Path.Combine(WorkRoot, "2", "old.mkv");
        await WriteOutput(output);
        Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(output)!, DateTime.UtcNow.AddDays(-8));
        await Reserve(FileClaimKeys.Media(int.MaxValue));
        await _recovery.RunAsync(default);
        await InDb(async db => { db.Jobs.Add(new() { MediaFileId = 2, LibraryId = 1, Status = JobStatus.Queued }); await db.SaveChangesAsync(); });
        await Release();
        Assert.True((await _recovery.RunAsync(default)).Complete);
        Assert.True(File.Exists(output));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("output")]
    [InlineData("replacement")]
    [InlineData("unknown")]
    [InlineData("lease")]
    public async Task Current_references_protect_old_numeric_directories(string reference)
    {
        var output = Path.Combine(WorkRoot, "2", "protected.mkv");
        await WriteOutput(output);
        Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(output)!, DateTime.UtcNow.AddDays(-8));
        await InDb(async db =>
        {
            if (reference == "source") (await db.MediaFiles.SingleAsync(f => f.Id == 2)).Path = output;
            else
            {
                var job = new Job { MediaFileId = reference is "unknown" or "lease" ? 2 : 1, LibraryId = 1,
                    Status = reference == "unknown" ? (JobStatus)999 : JobStatus.Completed,
                    WorkOutputPath = reference == "output" ? output : null };
                db.Jobs.Add(job); await db.SaveChangesAsync();
                if (reference == "replacement") db.Replacements.Add(new() { JobId = job.Id, MediaFileId = 1, QuarantinePath = output });
                if (reference == "lease") await AddHeldLease(db, job.Id);
            }
            await db.SaveChangesAsync();
        });
        Assert.True((await _recovery.RunAsync(default)).Complete);
        Assert.True(File.Exists(output));
    }

    [Fact]
    public async Task An_independently_held_lease_defers_cancelled_output_cleanup_until_released()
    {
        var job = await AddJob("cancelled"); await WriteOutput(job.WorkOutputPath!);
        await InDb(async db => { await AddHeldLease(db, job.Id); await db.SaveChangesAsync(); });
        Assert.False((await _recovery.RunAsync(default)).Complete);
        Assert.True(File.Exists(job.WorkOutputPath));
        await InDb(async db => { (await db.JobLeases.SingleAsync()).State = LeaseState.Released; await db.SaveChangesAsync(); });
        Assert.True((await _recovery.RunAsync(default)).Complete);
        Assert.False(File.Exists(job.WorkOutputPath));
    }

    [Fact]
    public async Task Failed_output_removal_keeps_the_recovery_state_and_reference_for_retry()
    {
        var job = await AddJob("interrupted");
        Directory.CreateDirectory(job.WorkOutputPath!);
        Assert.False((await _recovery.RunAsync(default)).Complete);
        Assert.Equal(JobStatus.Transcoding, (await ReadJob(job.Id)).Status);
        Assert.Equal(job.WorkOutputPath, (await ReadJob(job.Id)).WorkOutputPath);
        Directory.Delete(job.WorkOutputPath!);
        await WriteOutput(job.WorkOutputPath!);
        Assert.True((await _recovery.RunAsync(default)).Complete);
        Assert.Equal(JobStatus.Queued, (await ReadJob(job.Id)).Status);
    }

    [Fact]
    public async Task Delivered_verification_output_survives_startup_while_exhausted_local_work_fails()
    {
        var delivered = await AddJob("interrupted");
        var exhausted = await AddJob("interrupted");
        await InDb(async db =>
        {
            var job = await db.Jobs.SingleAsync(j => j.Id == delivered.Id);
            job.Status = JobStatus.Verifying;
            job.WorkOutputPath = RemoteCandidate.PathFor(Path.Combine(WorkRoot, "1"), job.Id, ".mkv");
            delivered.WorkOutputPath = job.WorkOutputPath;
            (await db.Jobs.SingleAsync(j => j.Id == exhausted.Id)).Attempt = 100;
            await db.SaveChangesAsync();
        });
        await WriteOutput(delivered.WorkOutputPath!); await WriteOutput(exhausted.WorkOutputPath!);
        Assert.True((await _recovery.RunAsync(default)).Complete);
        Assert.Equal(JobStatus.AwaitingVerification, (await ReadJob(delivered.Id)).Status);
        Assert.True(File.Exists(delivered.WorkOutputPath));
        Assert.Equal(JobStatus.Failed, (await ReadJob(exhausted.Id)).Status);
        Assert.False(File.Exists(exhausted.WorkOutputPath));
    }

    [Fact]
    public async Task Successful_startup_removes_only_captured_disposable_rows_and_job_trees()
    {
        var old = await AddJob("calibration");
        await InDb(async db => { (await db.Jobs.SingleAsync(j => j.Id == old.Id)).Status = JobStatus.Queued; await db.SaveChangesAsync(); });
        await WriteOutput(old.WorkOutputPath!);
        await Reserve(FileClaimKeys.Media(int.MaxValue));
        await _recovery.RunAsync(default);
        var fresh = await AddJob("calibration");
        await InDb(async db =>
        {
            var current = await db.Jobs.SingleAsync(j => j.Id == fresh.Id);
            current.CalibrationSessionId = old.CalibrationSessionId;
            current.WorkOutputPath = Path.Combine(WorkOutputRoot.ForCalibration(WorkRoot, old.CalibrationSessionId!.Value, fresh.Id), "output.mkv");
            fresh.WorkOutputPath = current.WorkOutputPath;
            await db.SaveChangesAsync();
        });
        await WriteOutput(fresh.WorkOutputPath!);
        await Release();
        Assert.True((await _recovery.RunAsync(default)).Complete);
        await InDb(async db => Assert.False(await db.Jobs.AnyAsync(j => j.Id == old.Id)));
        Assert.False(File.Exists(old.WorkOutputPath));
        Assert.True(File.Exists(fresh.WorkOutputPath));
        Assert.Equal(fresh.WorkOutputPath, (await ReadJob(fresh.Id)).WorkOutputPath);
    }

    [Theory]
    [InlineData("outside")]
    [InlineData("source")]
    [InlineData("busy")]
    public async Task Unsafe_or_in_use_scratch_paths_remain_referenced(string reason)
    {
        var job = await AddJob("preview");
        if (reason == "outside")
        {
            job.WorkOutputPath = Path.Combine(_root, "outside.mkv");
            await InDb(async db => { (await db.Jobs.SingleAsync(j => j.Id == job.Id)).WorkOutputPath = job.WorkOutputPath; await db.SaveChangesAsync(); });
        }
        if (reason == "source")
            await InDb(async db => { (await db.MediaFiles.SingleAsync(f => f.Id == 2)).Path = job.WorkOutputPath!; await db.SaveChangesAsync(); });
        await WriteOutput(job.WorkOutputPath!);
        var busy = reason == "busy";
        var busyDirectory = Path.GetFullPath(Path.GetDirectoryName(job.WorkOutputPath)!);
        _recovery = new(_services.GetRequiredService<IServiceScopeFactory>(), WorkRoot, _claims, NullLogger.Instance,
            p => busy && string.Equals(Path.GetFullPath(p), busyDirectory,
                OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase));
        Assert.False((await _recovery.RunAsync(default)).Complete);
        Assert.True(File.Exists(job.WorkOutputPath));
        Assert.Equal(job.WorkOutputPath, (await ReadJob(job.Id)).WorkOutputPath);
        if (busy)
        {
            busy = false;
            Assert.True((await _recovery.RunAsync(default)).Complete);
            Assert.False(File.Exists(job.WorkOutputPath));
        }
    }

    [Fact]
    public async Task Startup_writer_excludes_an_independent_reservation_until_cleanup_commits()
    {
        var job = await AddJob("preview"); await WriteOutput(job.WorkOutputPath!);
        _pause.Arm();
        var cleanup = Task.Run(() => _recovery.RunAsync(default));
        await _pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reservation = Task.Run(async () =>
        {
            attempted.SetResult();
            await InDb(async db => { await using var write = await db.BeginDiagnosticWriteAsync(); await ReserveIn(db); await write.CommitAsync(); });
        });
        try
        {
            await attempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(100);
            Assert.False(reservation.IsCompleted);
            Assert.True(File.Exists(job.WorkOutputPath));
        }
        finally { _pause.Continue.TrySetResult(); }
        Assert.True((await cleanup.WaitAsync(TimeSpan.FromSeconds(20))).Complete);
        await reservation.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.False(File.Exists(job.WorkOutputPath));
        Assert.Null((await ReadJob(job.Id)).WorkOutputPath);
    }

    [Fact]
    public async Task An_independent_reservation_writer_commits_before_startup_can_touch_files()
    {
        var job = await AddJob("preview"); await WriteOutput(job.WorkOutputPath!);
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        await using var write = await db.BeginDiagnosticWriteAsync();
        await ReserveIn(db);
        var attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = Task.Run(async () => { attempted.SetResult(); return await _recovery.RunAsync(default); });
        await attempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(100);
        Assert.False(cleanup.IsCompleted);
        Assert.True(File.Exists(job.WorkOutputPath));
        await write.CommitAsync();
        Assert.False((await cleanup.WaitAsync(TimeSpan.FromSeconds(20))).Complete);
        Assert.Equal(job.WorkOutputPath, (await ReadJob(job.Id)).WorkOutputPath);
        await Release();
        Assert.True((await _recovery.RunAsync(default)).Complete);
    }

    private async Task ReserveIn(OptimisarrDbContext db)
    {
        _reservation = Guid.NewGuid();
        db.DedupeActions.Add(new() { Id = _reservation.Value, ReviewKey = new('a', 64), ScopeKey = new('b', 64), PlanHash = new('c', 64) });
        db.DedupeFileClaims.Add(new() { ActionId = _reservation.Value, Key = FileClaimKeys.Media(int.MaxValue) });
        await db.SaveChangesAsync();
    }

    private StartupWorkRecovery NewRecovery() => new(_services.GetRequiredService<IServiceScopeFactory>(), WorkRoot, _claims, NullLogger.Instance, _ => false);
    private async Task<Job> AddJob(string kind)
    {
        var job = new Job { MediaFileId = 1, LibraryId = 1, Type = kind == "preview" ? JobType.Preview : kind == "calibration" ? JobType.Calibration : JobType.Normal,
            Status = kind is "preview" or "calibration" ? JobStatus.Failed : kind == "cancelled" ? JobStatus.Cancelled : JobStatus.Transcoding,
            Attempt = 1, EnqueuedAt = DateTimeOffset.UtcNow, ErrorMessage = "diagnostic evidence",
            CalibrationSessionId = kind == "calibration" ? Guid.NewGuid() : null };
        await InDb(async db => { db.Jobs.Add(job); await db.SaveChangesAsync();
            job.WorkOutputPath = kind == "preview" ? Path.Combine(WorkOutputRoot.ForPreview(WorkRoot, job.Id), "nested", "output.mkv")
                : kind == "calibration" ? Path.Combine(WorkOutputRoot.ForCalibration(WorkRoot, job.CalibrationSessionId!.Value, job.Id), "output.mkv")
                : Path.Combine(WorkRoot, "1", $"output-{job.Id}.mkv");
            await db.SaveChangesAsync(); });
        return job;
    }
    private static async Task WriteOutput(string path) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllTextAsync(path, "scratch evidence"); }
    private async Task<Job> ReadJob(int id)
    {
        using var scope = _services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>().Jobs.AsNoTracking().SingleAsync(j => j.Id == id);
    }
    private async Task InDb(Func<OptimisarrDbContext, Task> action)
    {
        using var scope = _services.CreateScope(); await action(scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>());
    }
    private async Task Reserve(string key)
    {
        _reservation = Guid.NewGuid();
        await InDb(async db => { db.DedupeActions.Add(new() { Id = _reservation.Value, ReviewKey = new('a', 64), ScopeKey = new('b', 64), PlanHash = new('c', 64) });
            db.DedupeFileClaims.Add(new() { ActionId = _reservation.Value, Key = key }); await db.SaveChangesAsync(); });
    }
    private async Task Release()
    {
        if (_reservation is not { } id) return;
        await InDb(async db => { db.DedupeFileClaims.RemoveRange(await db.DedupeFileClaims.Where(c => c.ActionId == id).ToListAsync()); await db.SaveChangesAsync();
            db.DedupeActions.Remove(await db.DedupeActions.SingleAsync(a => a.Id == id)); await db.SaveChangesAsync(); });
        _reservation = null;
    }
    private static async Task AddHeldLease(OptimisarrDbContext db, int jobId)
    {
        var worker = new Worker { Name = "Fixture worker", CredentialFingerprint = Guid.NewGuid().ToString() };
        db.Workers.Add(worker); await db.SaveChangesAsync();
        db.JobLeases.Add(new() { Id = Guid.NewGuid(), JobId = jobId, WorkerId = worker.Id, State = LeaseState.Held, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });
    }
    public async Task DisposeAsync() { await _services.DisposeAsync(); Directory.Delete(_root, true); }
    private sealed class ReservationPause : DbCommandInterceptor
    {
        private int _armed;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Arm() => Interlocked.Exchange(ref _armed, 1);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("DedupeActions", StringComparison.Ordinal)
                && Interlocked.Exchange(ref _armed, 0) == 1)
            {
                Entered.SetResult();
                await Continue.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }
}
