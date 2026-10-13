using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Optimisarr.Api.Deduplication;
using Optimisarr.Api.Queue;
using Optimisarr.Core.Deduplication;
using Optimisarr.Core.Workers;
using Optimisarr.Data;

namespace Optimisarr.Tests;

public sealed class QueueOutputCleanupTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "queue-cleanup-" + Guid.NewGuid());
    private readonly ReservationReadPause _pause = new();
    private DbContextOptions<OptimisarrDbContext> _options = null!;
    private string WorkRoot => Path.Combine(_root, "work");
    private string Output => Path.Combine(WorkRoot, "1", "candidate.mkv");
    private readonly FileOperationClaims _claims = new();

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Output)!);
        Directory.CreateDirectory(Path.Combine(_root, "library"));
        await File.WriteAllTextAsync(Output, "candidate evidence");
        _options = new DbContextOptionsBuilder<OptimisarrDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_root, "config.db")};Pooling=False").Options;
        await using var db = new OptimisarrDbContext(_options);
        await db.Database.MigrateAsync();
        db.Libraries.Add(new() { Id = 1, Name = "Fixtures", Path = Path.Combine(_root, "library") });
        db.MediaFiles.Add(new() { Id = 1, LibraryId = 1, Path = Path.Combine(_root, "library", "source.mkv"), RelativePath = "source.mkv" });
        db.Jobs.Add(new() { Id = 1, MediaFileId = 1, LibraryId = 1, Status = JobStatus.Failed,
            WorkOutputPath = Output, ErrorMessage = "diagnostic evidence" });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Cleanup_waits_for_an_independent_writer_and_keeps_its_committed_reservation()
    {
        await using var writer = new OptimisarrDbContext(_options);
        await using var transaction = await writer.BeginDiagnosticWriteAsync();
        Reserve(writer);
        await writer.SaveChangesAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = Task.Run(async () => { started.SetResult(); return await CleanAsync(); });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try { await Task.Delay(100); Assert.False(cleanup.IsCompleted); }
        finally { await transaction.CommitAsync(); }
        Assert.False(await cleanup.WaitAsync(TimeSpan.FromSeconds(10)));
        await AssertRetainedAsync();
    }

    [Fact]
    public async Task An_independent_reservation_cannot_commit_between_output_deletion_and_the_job_transition()
    {
        _pause.Enabled = true;
        var cleanup = CleanAsync();
        await _pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reservation = Task.Run(async () =>
        {
            await using var db = new OptimisarrDbContext(_options);
            started.SetResult();
            await using var write = await db.BeginDiagnosticWriteAsync();
            Reserve(db);
            await db.SaveChangesAsync();
            await write.CommitAsync();
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try { await Task.Delay(100); Assert.False(reservation.IsCompleted); Assert.True(File.Exists(Output)); }
        finally { _pause.Release.SetResult(); }
        Assert.True(await cleanup.WaitAsync(TimeSpan.FromSeconds(10)));
        await reservation.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(File.Exists(Output));
        await using var verify = new OptimisarrDbContext(_options);
        Assert.Null((await verify.Jobs.AsNoTracking().SingleAsync()).WorkOutputPath);
    }

    [Fact]
    public async Task Cancellation_during_the_guard_keeps_files_and_diagnostics_and_releases_process_claims()
    {
        _pause.Enabled = true;
        using var cancellation = new CancellationTokenSource();
        var cleanup = CleanAsync(cancellation.Token);
        await _pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cleanup);
        await AssertRetainedAsync();
        using var held = _claims.TryAcquire(["after cancellation"]);
        Assert.NotNull(held);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("other-source")]
    [InlineData("lease")]
    [InlineData("replacement")]
    [InlineData("library")]
    public async Task Cleanup_refuses_source_worker_and_recovery_ownership(string owner)
    {
        await using (var db = new OptimisarrDbContext(_options))
        {
            if (owner == "library") (await db.Libraries.SingleAsync()).Path = WorkRoot;
            if (owner == "source") (await db.MediaFiles.SingleAsync()).Path = Output;
            if (owner == "other-source") db.MediaFiles.Add(new() { LibraryId = 1, Path = Output, RelativePath = "elsewhere.mkv" });
            if (owner == "lease")
            {
                db.Workers.Add(new() { Id = 1, Name = "Fixture" });
                db.JobLeases.Add(new() { Id = Guid.NewGuid(), JobId = 1, WorkerId = 1, State = LeaseState.Held });
            }
            if (owner == "replacement") db.Replacements.Add(new() { JobId = 1, MediaFileId = 1,
                Status = ReplacementStatus.Pending, OriginalPath = Output, QuarantinePath = "unused", FinalPath = "unused" });
            await db.SaveChangesAsync();
        }
        Assert.False(await CleanAsync());
        await AssertRetainedAsync();
    }

    [Fact]
    public async Task Missing_output_cleanup_is_idempotent_and_keeps_the_diagnostic_row()
    {
        File.Delete(Output);
        Assert.True(await CleanAsync());
        Assert.True(await CleanAsync());
        await using var db = new OptimisarrDbContext(_options);
        Assert.Equal("diagnostic evidence", (await db.Jobs.AsNoTracking().SingleAsync()).ErrorMessage);
    }

    [Fact]
    public async Task Missing_parent_directories_do_not_strand_a_resettable_job()
    {
        Directory.Delete(Path.GetDirectoryName(Output)!, true);
        Assert.True(await CleanAsync());
    }

    [Fact]
    public async Task A_running_encode_directory_prevents_output_deletion()
    {
        await using var db = new OptimisarrDbContext(_options);
        await using var write = await db.BeginDiagnosticWriteAsync();
        var job = await db.Jobs.SingleAsync();
        Assert.False(await QueueOutputCleanup.TryDiscardAsync(db, _claims, job, WorkRoot, _ => true, default));
        await AssertRetainedAsync();
    }

    [Fact]
    public async Task Cleanup_requires_the_callers_writer_transaction()
    {
        await using var db = new OptimisarrDbContext(_options);
        var job = await db.Jobs.SingleAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            QueueOutputCleanup.TryDiscardAsync(db, _claims, job, WorkRoot, _ => false, default));
        await AssertRetainedAsync();
    }

    [Fact]
    public async Task Cleanup_rejects_a_directory_recorded_as_a_file_output()
    {
        File.Delete(Output);
        Directory.CreateDirectory(Output);
        Assert.False(await CleanAsync());
        await using var db = new OptimisarrDbContext(_options);
        Assert.Equal(Output, (await db.Jobs.AsNoTracking().SingleAsync()).WorkOutputPath);
    }

    [Fact]
    public async Task Cleanup_preserves_a_source_that_resolves_to_the_output_through_a_symlink()
    {
        if (OperatingSystem.IsWindows()) return;
        File.CreateSymbolicLink(Path.Combine(_root, "library", "source.mkv"), Output);
        Assert.False(await CleanAsync());
        await AssertRetainedAsync();
    }

    private async Task<bool> CleanAsync(CancellationToken token = default)
    {
        var options = new DbContextOptionsBuilder<OptimisarrDbContext>(_options).AddInterceptors(_pause).Options;
        await using var db = new OptimisarrDbContext(options);
        await using var write = await db.BeginDiagnosticWriteAsync(token);
        var job = await db.Jobs.SingleAsync(token);
        if (!await QueueOutputCleanup.TryDiscardAsync(db, _claims, job, WorkRoot, _ => false, token)) return false;
        job.WorkOutputPath = null;
        await db.SaveChangesAsync(token);
        await write.CommitAsync(token);
        return true;
    }

    private async Task AssertRetainedAsync()
    {
        Assert.Equal("candidate evidence", await File.ReadAllTextAsync(Output));
        await using var db = new OptimisarrDbContext(_options);
        var job = await db.Jobs.AsNoTracking().SingleAsync();
        Assert.Equal(Output, job.WorkOutputPath);
        Assert.Equal("diagnostic evidence", job.ErrorMessage);
        Assert.Equal(JobStatus.Failed, job.Status);
    }

    private static void Reserve(OptimisarrDbContext db)
    {
        var id = Guid.NewGuid();
        db.DedupeActions.Add(new() { Id = id, State = DedupeActionState.Prepared,
            ReviewKey = new('a', 64), ScopeKey = new('b', 64), PlanHash = new('c', 64) });
        db.DedupeFileClaims.Add(new() { ActionId = id, Key = FileClaimKeys.Media(1) });
    }

    public Task DisposeAsync() { Directory.Delete(_root, true); return Task.CompletedTask; }

    private sealed class ReservationReadPause : DbCommandInterceptor
    {
        public bool Enabled { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (Enabled && command.CommandText.Contains("EXISTS") && command.CommandText.Contains("DedupeActions"))
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }
}
