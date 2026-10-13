using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Optimisarr.Api.Deduplication;
using Optimisarr.Api.Queue;
using Optimisarr.Core.Deduplication;
using Optimisarr.Data;

namespace Optimisarr.Tests;

public sealed class OwnedScratchWorkspaceTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "owned-scratch-" + Guid.NewGuid());
    private readonly FileOperationClaims _claims = new();
    private readonly ReservationReadPause _pause = new();
    private ServiceProvider _services = null!;
    private string WorkRoot => Path.Combine(_root, "work");
    private string Parent => Path.Combine(WorkRoot, "1");
    private string Original => Path.Combine(_root, "library", "source.mkv");
    private string Candidate => Path.Combine(Parent, "candidate.mkv");
    private Guid? _reservation;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Parent);
        Directory.CreateDirectory(Path.GetDirectoryName(Original)!);
        await File.WriteAllTextAsync(Original, "unchanged original");
        await File.WriteAllTextAsync(Candidate, "retained main candidate");
        var services = new ServiceCollection();
        services.AddDbContext<OptimisarrDbContext>(o => o.UseSqlite($"Data Source={Path.Combine(_root, "config.db")};Pooling=False").AddInterceptors(_pause));
        _services = services.BuildServiceProvider();
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        await db.Database.MigrateAsync();
        db.Libraries.Add(new() { Id = 1, Name = "Fixture", Path = Path.GetDirectoryName(Original)! });
        db.MediaFiles.Add(new() { Id = 1, LibraryId = 1, Path = Original, RelativePath = "source.mkv" });
        db.Jobs.Add(new() { Id = 1, MediaFileId = 1, LibraryId = 1, Status = JobStatus.Probing,
            ExecutionAttempt = 2, WorkOutputPath = Candidate, ProcessLog = "retained diagnostics" });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Sample_cleanup_preserves_the_workspace_for_the_next_window()
    {
        var workspace = await CreateAsync();
        var first = await SampleAsync(workspace, "q20-window0.mp4");
        Assert.True(await workspace.TryDeleteAsync(first));
        Assert.False(File.Exists(first));
        Assert.True(Directory.Exists(workspace.Root));
        var second = await SampleAsync(workspace, "q20-window1.mp4");
        await workspace.DisposeAsync();
        Assert.False(File.Exists(second));
        Assert.False(Directory.Exists(workspace.Root));
        await AssertMainEvidenceAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Adaptive_sample_cleanup_keeps_reserved_files_and_can_retry_after_release(bool busy)
    {
        var workspace = await CreateAsync();
        var sample = await SampleAsync(workspace);
        using var held = busy ? _claims.TryAcquire(["another file operation"]) : null;
        if (!busy) await ReserveAsync();
        Assert.False(await workspace.TryDeleteAsync(sample));
        await workspace.DisposeAsync();
        Assert.Equal("sample evidence", await File.ReadAllTextAsync(sample));
        Assert.True(File.Exists(Path.Combine(workspace.Root, ".optimisarr-owner")));
        held?.Dispose();
        await ReleaseAsync();
        Assert.True(await workspace.TryDeleteAsync(sample));
        await workspace.DisposeAsync();
        Assert.False(Directory.Exists(workspace.Root));
        await AssertMainEvidenceAsync();
    }

    [Theory]
    [InlineData("foreign-file")]
    [InlineData("nested-folder")]
    public async Task Workspace_cleanup_never_recursively_deletes_unregistered_content(string kind)
    {
        var workspace = await CreateAsync();
        var sample = await SampleAsync(workspace);
        var foreign = Path.Combine(workspace.Root, "foreign");
        if (kind == "foreign-file") await File.WriteAllTextAsync(foreign, "foreign evidence");
        else Directory.CreateDirectory(foreign);
        await workspace.DisposeAsync();
        Assert.False(File.Exists(sample));
        Assert.True(File.Exists(foreign) || Directory.Exists(foreign));
        Assert.True(File.Exists(Path.Combine(workspace.Root, ".optimisarr-owner")));
        await AssertMainEvidenceAsync();
    }

    [Theory]
    [InlineData("changed-file")]
    [InlineData("changed-marker")]
    [InlineData("missing-marker")]
    [InlineData("new-attempt")]
    [InlineData("new-media")]
    [InlineData("new-kind")]
    [InlineData("source-reference")]
    [InlineData("other-job")]
    public async Task Cleanup_defers_changed_generations_owners_and_new_references(string change)
    {
        var workspace = await CreateAsync();
        var sample = await SampleAsync(workspace);
        if (change == "changed-file") await File.WriteAllTextAsync(sample, "changed file generation");
        if (change == "changed-marker") await File.WriteAllTextAsync(Path.Combine(workspace.Root, ".optimisarr-owner"), "changed owner");
        if (change == "missing-marker") File.Delete(Path.Combine(workspace.Root, ".optimisarr-owner"));
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            if (change == "new-attempt") (await db.Jobs.SingleAsync()).ExecutionAttempt = 3;
            if (change == "new-kind") (await db.Jobs.SingleAsync()).Type = JobType.Preview;
            if (change == "new-media")
            {
                db.MediaFiles.Add(new() { Id = 2, LibraryId = 1, Path = Path.Combine(_root, "other.mkv"), RelativePath = "other.mkv" });
                (await db.Jobs.SingleAsync()).MediaFileId = 2;
            }
            if (change == "source-reference") db.MediaFiles.Add(new() { LibraryId = 1, Path = sample, RelativePath = "sample.mkv" });
            if (change == "other-job") db.Jobs.Add(new() { MediaFileId = 1, LibraryId = 1, Status = JobStatus.Failed, WorkOutputPath = sample });
            await db.SaveChangesAsync();
        }
        // A repeated capture cannot adopt different bytes or a new owner.
        workspace.Capture(sample);
        Assert.False(await workspace.TryDeleteAsync(sample));
        await workspace.DisposeAsync();
        Assert.True(File.Exists(sample));
        await AssertMainEvidenceAsync();
    }

    [Fact]
    public async Task A_deleted_name_cannot_be_adopted_again_by_a_duplicate_callback()
    {
        var workspace = await CreateAsync();
        var sample = await SampleAsync(workspace);
        Assert.True(await workspace.TryDeleteAsync(sample));
        await File.WriteAllTextAsync(sample, "replacement file");
        workspace.Capture(sample);
        Assert.False(await workspace.TryDeleteAsync(sample));
        await workspace.DisposeAsync();
        Assert.Equal("replacement file", await File.ReadAllTextAsync(sample));
    }

    [Fact]
    public async Task Missing_outputs_are_idempotent_and_do_not_leave_an_empty_workspace()
    {
        var workspace = await CreateAsync();
        var sample = Path.Combine(workspace.Root, "not-created.mkv");
        workspace.Capture(sample);
        Assert.True(await workspace.TryDeleteAsync(sample));
        await workspace.DisposeAsync();
        await workspace.DisposeAsync();
        Assert.False(Directory.Exists(workspace.Root));
    }

    [Theory]
    [InlineData("reservation")]
    [InlineData("busy")]
    [InlineData("wrong-attempt")]
    [InlineData("wrong-parent")]
    public async Task Scratch_creation_is_fenced_before_any_workspace_is_created(string conflict)
    {
        if (conflict == "reservation") await ReserveAsync();
        using var held = conflict == "busy" ? _claims.TryAcquire(["another operation"]) : null;
        await Assert.ThrowsAsync<FileCleanupDeferredException>(() => CreateAsync(
            conflict == "wrong-attempt" ? 1 : 2, conflict == "wrong-parent" ? Path.GetDirectoryName(Original) : null));
        Assert.Single(Directory.EnumerateFileSystemEntries(Parent));
        await AssertMainEvidenceAsync();
    }

    [Fact]
    public async Task A_workspace_cannot_capture_a_main_candidate_or_an_original_outside_its_root()
    {
        await using var workspace = await CreateAsync();
        Assert.Throws<InvalidOperationException>(() => workspace.Capture(Candidate));
        Assert.Throws<InvalidOperationException>(() => workspace.Capture(Original));
        await AssertMainEvidenceAsync();
    }

    [Fact]
    public async Task Cleanup_observes_a_separate_writers_committed_reservation()
    {
        var workspace = await CreateAsync();
        var sample = await SampleAsync(workspace);
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        await using var write = await db.BeginDiagnosticWriteAsync();
        AddReservation(db);
        await db.SaveChangesAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = Task.Run(async () => { started.SetResult(); return await workspace.TryDeleteAsync(sample); });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try { await Task.Delay(100); Assert.False(cleanup.IsCompleted); }
        finally { await write.CommitAsync(); }
        Assert.False(await cleanup.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(File.Exists(sample));
        await ReleaseAsync();
        await workspace.DisposeAsync();
        Assert.False(Directory.Exists(workspace.Root));
    }

    [Fact]
    public async Task A_separate_reservation_waits_until_sample_cleanup_commits()
    {
        var workspace = await CreateAsync();
        var sample = await SampleAsync(workspace);
        _pause.Enabled = true;
        var cleanup = workspace.TryDeleteAsync(sample);
        Task reservation = Task.CompletedTask;
        try
        {
            await _pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            reservation = Task.Run(async () => { started.SetResult(); await ReserveAsync(); });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(100);
            Assert.False(reservation.IsCompleted);
            Assert.True(File.Exists(sample));
        }
        finally { _pause.Release.TrySetResult(); }
        Assert.True(await cleanup.WaitAsync(TimeSpan.FromSeconds(10)));
        await reservation.WaitAsync(TimeSpan.FromSeconds(10));
        await workspace.DisposeAsync();
        Assert.True(File.Exists(Path.Combine(workspace.Root, ".optimisarr-owner")));
        await ReleaseAsync();
        await workspace.DisposeAsync();
        Assert.False(Directory.Exists(workspace.Root));
    }

    [Fact]
    public async Task Unavailable_cleanup_services_do_not_remove_files_or_throw_over_a_completed_measurement()
    {
        var workspace = await CreateAsync();
        var sample = await SampleAsync(workspace);
        await _services.DisposeAsync();
        Assert.False(await workspace.TryDeleteAsync(sample));
        await workspace.DisposeAsync();
        Assert.Equal("sample evidence", await File.ReadAllTextAsync(sample));
    }

    [PosixFact]
    public async Task Redirected_workspace_parents_are_refused_before_creation()
    {
        var redirect = Path.Combine(Parent, "redirect");
        Directory.CreateSymbolicLink(redirect, Path.GetDirectoryName(Original)!);
        await Assert.ThrowsAsync<FileCleanupDeferredException>(() => CreateAsync(parent: redirect));
        Assert.Single(Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(Original)!));
        await AssertMainEvidenceAsync();
    }

    private Task<OwnedScratchWorkspace> CreateAsync(int attempt = 2, string? parent = null) =>
        OwnedScratchWorkspace.CreateAsync(_services.GetRequiredService<IServiceScopeFactory>(), _claims,
            WorkRoot, 1, attempt, parent ?? Parent, "adaptive-quality", NullLogger.Instance, default);

    private static async Task<string> SampleAsync(OwnedScratchWorkspace workspace, string name = "sample.mkv")
    {
        var sample = Path.Combine(workspace.Root, name);
        await File.WriteAllTextAsync(sample, "sample evidence");
        workspace.Capture(sample);
        return sample;
    }

    private async Task ReserveAsync()
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        AddReservation(db);
        await db.SaveChangesAsync();
    }

    private void AddReservation(OptimisarrDbContext db)
    {
        _reservation = Guid.NewGuid();
        db.DedupeActions.Add(new() { Id = _reservation.Value, State = (DedupeActionState)999,
            ReviewKey = new('a', 64), ScopeKey = new('b', 64), PlanHash = new('c', 64) });
    }

    private async Task ReleaseAsync()
    {
        if (_reservation is not { } id) return;
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        db.DedupeActions.Remove(await db.DedupeActions.SingleAsync(a => a.Id == id));
        await db.SaveChangesAsync();
        _reservation = null;
    }

    private async Task AssertMainEvidenceAsync()
    {
        Assert.Equal("unchanged original", await File.ReadAllTextAsync(Original));
        Assert.Equal("retained main candidate", await File.ReadAllTextAsync(Candidate));
        using var scope = _services.CreateScope();
        var job = await scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>().Jobs.AsNoTracking().SingleAsync(j => j.Id == 1);
        Assert.Equal(Candidate, job.WorkOutputPath);
        Assert.Equal("retained diagnostics", job.ProcessLog);
    }

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

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        Directory.Delete(_root, true);
    }
}
