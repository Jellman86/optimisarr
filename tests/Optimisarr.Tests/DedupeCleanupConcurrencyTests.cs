using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Optimisarr.Api.Deduplication;
using Optimisarr.Api.Library;
using Optimisarr.Api.Replacement;
using Optimisarr.Core.Deduplication;
using Optimisarr.Data;

namespace Optimisarr.Tests;

public sealed class DedupeCleanupConcurrencyTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "optimisarr-cleanup-race-" + Guid.NewGuid());
    private readonly ReservationReadPause _pause = new();
    private ServiceProvider _services = null!;
    private DbContextOptions<OptimisarrDbContext> _options = null!;
    private string CopyPath => Path.Combine(_root, "work", "copy.mkv");
    private DedupePreparation Plan => new(Guid.NewGuid(), 1, new('a', 64), new('b', 64),
    [
        new(1, 1, _root, Path.Combine(_root, "keeper.mkv"), null, "disk:1", 42, 1, new('c', 64), true),
        new(2, 1, _root, CopyPath, Path.Combine(_root, "quarantine", "copy"), "disk:2", 42, 1, new('c', 64), false)
    ]);

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CopyPath)!);
        await File.WriteAllTextAsync(CopyPath, "retained candidate");
        File.SetLastWriteTimeUtc(CopyPath, DateTime.UtcNow.AddDays(-8));
        _options = new DbContextOptionsBuilder<OptimisarrDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_root, "journal.db")};Pooling=False").Options;
        var services = new ServiceCollection();
        services.AddDbContext<OptimisarrDbContext>(o => o.UseSqlite($"Data Source={Path.Combine(_root, "journal.db")};Pooling=False"));
        services.AddSingleton<FileOperationClaims>();
        services.AddSingleton<DedupeJournalStore>();
        _services = services.BuildServiceProvider();
        await using var db = new OptimisarrDbContext(_options);
        await db.Database.MigrateAsync();
        db.Libraries.Add(new() { Id = 1, Name = "Fixtures", Path = _root });
        db.MediaFiles.AddRange(Plan.Files.Select(f => new MediaFile { Id = f.MediaFileId, LibraryId = 1,
            Path = f.Path, RelativePath = Path.GetFileName(f.Path) }));
        await db.SaveChangesAsync();
        db.Jobs.Add(new() { MediaFileId = 2, Status = JobStatus.Failed, WorkOutputPath = CopyPath,
            FinishedAt = DateTimeOffset.UtcNow.AddDays(-8) });
        await db.SaveChangesAsync();
        var settings = new SettingsStore(db);
        var current = await settings.GetQueueSettingsAsync(default);
        await settings.SetQueueSettingsAsync(current with { ReplacementQuarantineRetentionDays = 7 }, default);
    }

    [Fact]
    public async Task Cleanup_waits_for_a_separate_writer_and_observes_its_committed_reservation()
    {
        await using var writer = new OptimisarrDbContext(_options);
        await using var transaction = await writer.BeginDiagnosticWriteAsync();
        var action = new DedupeAction { Id = Guid.NewGuid(), ReviewKey = new('a', 64), ScopeKey = new('b', 64), PlanHash = new('c', 64) };
        writer.DedupeActions.Add(action);
        writer.DedupeFileClaims.Add(new() { ActionId = action.Id, Key = FileClaimKeys.Media(2) });
        await writer.SaveChangesAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = Task.Run(async () => { started.SetResult(); return await CleanAsync(); });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await Task.Delay(100);
            Assert.False(cleanup.IsCompleted);
        }
        finally { await transaction.CommitAsync(); }
        Assert.Equal(0, await cleanup.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("retained candidate", await File.ReadAllTextAsync(CopyPath));
        await using var verify = new OptimisarrDbContext(_options);
        Assert.Equal(CopyPath, (await verify.Jobs.AsNoTracking().SingleAsync()).WorkOutputPath);
    }

    [Fact]
    public async Task Preparation_on_an_independent_coordinator_waits_until_cleanup_finishes_its_mutation_and_commit()
    {
        _pause.Enabled = true;
        var cleanup = CleanAsync();
        await _pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // This separate coordinator deliberately has no access to the cleanup process's memory claims.
        var preparation = Task.Run(async () =>
        {
            started.SetResult();
            return await _services.GetRequiredService<DedupeJournalStore>().TryPrepareAsync(Plan, default);
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await Task.Delay(100);
            Assert.False(preparation.IsCompleted);
            Assert.True(File.Exists(CopyPath));
        }
        finally { _pause.Release.SetResult(); }
        Assert.Equal(1, await cleanup.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(DedupePreparationResult.Created, await preparation.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(File.Exists(CopyPath));
        // Preparation records cached evidence only. Fresh preflight must reject this vanished file;
        // the journal does not enable or perform an action.
    }

    [Fact]
    public async Task A_mutation_guard_requires_the_authoritative_writer_and_releases_conflicting_claims()
    {
        await using var db = new OptimisarrDbContext(_options);
        var claims = new FileOperationClaims();
        await Assert.ThrowsAsync<InvalidOperationException>(() => FileMutationGuard.TryAcquireAsync(db, claims, 2, [CopyPath], default));
        var store = _services.GetRequiredService<DedupeJournalStore>();
        var plan = Plan;
        Assert.Equal(DedupePreparationResult.Created, await store.TryPrepareAsync(plan, default));
        await using (var write = await db.BeginDiagnosticWriteAsync())
            Assert.Null(await FileMutationGuard.TryAcquireAsync(db, claims, 2, [CopyPath], default));
        Assert.True(await store.CancelPreparedAsync(plan.Id, default));
        await using var retry = await db.BeginDiagnosticWriteAsync();
        using var held = await FileMutationGuard.TryAcquireAsync(db, claims, 2, [CopyPath], default);
        Assert.NotNull(held);
    }

    private async Task<int> CleanAsync()
    {
        var options = new DbContextOptionsBuilder<OptimisarrDbContext>(_options).AddInterceptors(_pause).Options;
        await using var db = new OptimisarrDbContext(options);
        var service = new TimedCleanupService(db, new SettingsStore(db), Path.Combine(_root, "work"),
            NullLogger<TimedCleanupService>.Instance, new FileOperationClaims());
        return await service.PurgeExpiredAsync(default);
    }

    [Theory]
    [InlineData(DedupeActionState.Prepared, false, false)]
    [InlineData((DedupeActionState)999, false, false)]
    [InlineData(DedupeActionState.Cancelled, true, false)]
    [InlineData(DedupeActionState.Cancelled, false, true)]
    [InlineData(DedupeActionState.Restored, false, true)]
    public async Task Recursive_cleanup_refuses_incomplete_unresolved_intents_and_leftover_claims(
        DedupeActionState state, bool addClaim, bool allowed)
    {
        await using var db = new OptimisarrDbContext(_options);
        var claims = new FileOperationClaims();
        await Assert.ThrowsAsync<InvalidOperationException>(() => FileMutationGuard.TryAcquireRecursiveCleanupAsync(db, claims, default));
        var action = new DedupeAction { Id = Guid.NewGuid(), State = state, ReviewKey = new('a', 64), ScopeKey = new('b', 64), PlanHash = new('c', 64) };
        db.DedupeActions.Add(action);
        if (addClaim) db.DedupeFileClaims.Add(new() { ActionId = action.Id, Key = FileClaimKeys.Media(999) });
        await db.SaveChangesAsync();
        await using var write = await db.BeginDiagnosticWriteAsync();
        using (var guard = await FileMutationGuard.TryAcquireRecursiveCleanupAsync(db, claims, default))
            Assert.Equal(allowed, guard is not null);
        using var retry = claims.TryAcquire(["after guard"]);
        Assert.NotNull(retry);
    }

    [Fact]
    public async Task Recursive_cleanup_observes_a_separate_writers_reservation_before_deleting_any_descendant()
    {
        await using var writer = new OptimisarrDbContext(_options);
        await using var transaction = await writer.BeginDiagnosticWriteAsync();
        var action = new DedupeAction { Id = Guid.NewGuid(), ReviewKey = new('a', 64), ScopeKey = new('b', 64), PlanHash = new('c', 64) };
        writer.DedupeActions.Add(action);
        writer.DedupeFileClaims.Add(new() { ActionId = action.Id, Key = FileClaimKeys.Path(CopyPath, false) });
        await writer.SaveChangesAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = Task.Run(async () => { started.SetResult(); return await CleanRecursiveAsync(); });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try { await Task.Delay(100); Assert.False(cleanup.IsCompleted); }
        finally { await transaction.CommitAsync(); }
        Assert.False(await cleanup.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("retained candidate", await File.ReadAllTextAsync(CopyPath));
        await using var verify = new OptimisarrDbContext(_options);
        Assert.Equal(CopyPath, (await verify.Jobs.AsNoTracking().SingleAsync()).WorkOutputPath);
    }

    [Fact]
    public async Task Independent_preparation_waits_until_recursive_cleanup_commits_its_files_and_rows()
    {
        _pause.Enabled = true;
        var cleanup = CleanRecursiveAsync();
        await _pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var preparation = Task.Run(async () =>
        {
            started.SetResult();
            return await _services.GetRequiredService<DedupeJournalStore>().TryPrepareAsync(Plan, default);
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try { await Task.Delay(100); Assert.False(preparation.IsCompleted); Assert.True(File.Exists(CopyPath)); }
        finally { _pause.Release.SetResult(); }
        Assert.True(await cleanup.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(DedupePreparationResult.Created, await preparation.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(File.Exists(CopyPath));
        await using var verify = new OptimisarrDbContext(_options);
        Assert.Null((await verify.Jobs.AsNoTracking().SingleAsync()).WorkOutputPath);
        // Mechanical preparation may persist cached evidence; fresh D3c preflight is still required.
    }

    private async Task<bool> CleanRecursiveAsync()
    {
        var options = new DbContextOptionsBuilder<OptimisarrDbContext>(_options).AddInterceptors(_pause).Options;
        await using var db = new OptimisarrDbContext(options);
        await using var write = await db.BeginDiagnosticWriteAsync();
        using var guard = await FileMutationGuard.TryAcquireRecursiveCleanupAsync(db, new FileOperationClaims(), default);
        if (guard is null) return false;
        Directory.Delete(Path.GetDirectoryName(CopyPath)!, recursive: true);
        (await db.Jobs.SingleAsync()).WorkOutputPath = null;
        await db.SaveChangesAsync();
        await write.CommitAsync();
        return true;
    }

    [Fact]
    public async Task Cancellation_during_recursive_reservation_reads_releases_the_exclusive_claim()
    {
        await using var db = new OptimisarrDbContext(_options);
        await using var write = await db.BeginDiagnosticWriteAsync();
        var claims = new FileOperationClaims();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            FileMutationGuard.TryAcquireRecursiveCleanupAsync(db, claims, cancellation.Token));
        using var retry = await FileMutationGuard.TryAcquireRecursiveCleanupAsync(db, claims, default);
        Assert.NotNull(retry);
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        Directory.Delete(_root, true);
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
            if (Enabled && command.CommandText.Contains("EXISTS")
                && (command.CommandText.Contains("DedupeFileClaims") || command.CommandText.Contains("DedupeActions")))
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }
}
