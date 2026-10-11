using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Optimisarr.Api.Deduplication;
using Optimisarr.Core.Deduplication;
using Optimisarr.Data;

namespace Optimisarr.Tests;

public sealed class DedupeJournalStoreTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "optimisarr-dedupe-journal-" + Guid.NewGuid());
    private ServiceProvider _services = null!;
    private DedupeJournalStore Store => _services.GetRequiredService<DedupeJournalStore>();
    private DedupePreparation Plan(Guid? id = null, int offset = 0) => new(id ?? Guid.NewGuid(), 1, new('a',64), new('b',64),
    [
        new(1 + offset, 1, _root, Path.Combine(_root,$"keeper-{offset}.mkv"), null, $"disk:{1 + offset}", 42, 1, new('c',64), true),
        new(2 + offset, 1, _root, Path.Combine(_root,$"copy-{offset}.mkv"), Path.Combine(_root,".optimisarr-dedupe",$"copy-{offset}"), $"disk:{2 + offset}", 42, 1, new('c',64), false)
    ]);

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        var services = new ServiceCollection();
        services.AddDbContext<OptimisarrDbContext>(o => o.UseSqlite($"Data Source={Path.Combine(_root,"journal.db")};Pooling=False"));
        services.AddSingleton<FileOperationClaims>();
        services.AddSingleton<DedupeJournalStore>();
        _services = services.BuildServiceProvider();
        await using var scope = _services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>().Database.MigrateAsync();
    }
    public async Task DisposeAsync() { await _services.DisposeAsync(); Directory.Delete(_root,true); }

    [Fact]
    public async Task Preparation_records_all_rollback_paths_and_retention_before_any_move()
    {
        var plan = Plan();
        Assert.Equal(DedupePreparationResult.Created, await Store.TryPrepareAsync(plan, default));
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var row = await db.DedupeActions.AsNoTracking().SingleAsync();
        Assert.Equal(DedupeActionState.Prepared, row.State);
        Assert.Equal("UntilRestored", row.Retention);
        Assert.Equal(DedupePreparationPolicy.Signature(plan), row.PlanHash);
        var files = await db.DedupeActionFiles.AsNoTracking().OrderBy(f => f.MediaFileId).ToListAsync();
        Assert.Equal(plan.Files.Select(f => f.Path), files.Select(f => f.Path));
        Assert.Equal(plan.Files[1].QuarantinePath, files[1].QuarantinePath);
        Assert.Equal(7, await db.DedupeFileClaims.CountAsync());
        Assert.False(Directory.Exists(Path.Combine(_root,".optimisarr-dedupe")));
        await db.Database.MigrateAsync();
        Assert.Equal(2, await db.DedupeActionFiles.CountAsync());
    }

    [Fact]
    public async Task Retry_is_idempotent_but_a_reused_action_id_cannot_change_its_plan()
    {
        var plan = Plan();
        Assert.Equal(DedupePreparationResult.Created, await Store.TryPrepareAsync(plan, default));
        Assert.Equal(DedupePreparationResult.AlreadyRecorded, await Store.TryPrepareAsync(plan with { Files = plan.Files.Reverse().ToArray() }, default));
        Assert.Equal(DedupePreparationResult.Conflict, await Store.TryPrepareAsync(plan with { ReviewKey = new('d',64) }, default));
        await using var scope = _services.CreateAsyncScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>().DedupeActions.CountAsync());
    }

    [Fact]
    public async Task A_durable_identity_or_destination_claim_blocks_another_preparation_and_rolls_back_all_rows()
    {
        var first = Plan();
        Assert.Equal(DedupePreparationResult.Created, await Store.TryPrepareAsync(first, default));
        var other = Plan(offset:10);
        var alias = other with { Files = [other.Files[0] with { Identity = first.Files[0].Identity },other.Files[1]] };
        Assert.Equal(DedupePreparationResult.Conflict, await Store.TryPrepareAsync(alias, default));
        var destination = other with { Files = [other.Files[0],other.Files[1] with { QuarantinePath = first.Files[1].QuarantinePath }] };
        Assert.Equal(DedupePreparationResult.Conflict, await Store.TryPrepareAsync(destination, default));
        Assert.Equal(DedupePreparationResult.Created, await Store.TryPrepareAsync(other, default));
        await using var scope = _services.CreateAsyncScope();
        Assert.Equal(2, await scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>().DedupeActions.CountAsync());
    }

    [Fact]
    public async Task Prepared_cancellation_releases_claims_atomically_but_retry_never_reactivates_it()
    {
        var plan = Plan();
        await Store.TryPrepareAsync(plan, default);
        Assert.True(await Store.CancelPreparedAsync(plan.Id,default));
        Assert.True(await Store.CancelPreparedAsync(plan.Id,default));
        Assert.Equal(DedupePreparationResult.AlreadyRecorded,await Store.TryPrepareAsync(plan,default));
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        Assert.Empty(await db.DedupeFileClaims.ToListAsync());
        Assert.Equal(DedupeActionState.Cancelled,(await db.DedupeActions.SingleAsync()).State);
        var second = plan with { Id = Guid.NewGuid(), Files = [plan.Files[0], plan.Files[1] with { QuarantinePath = Path.Combine(_root,"other-quarantine") }] };
        Assert.Equal(DedupePreparationResult.Created,await Store.TryPrepareAsync(second,default));
    }

    [Theory]
    [InlineData(DedupeActionState.Prepared)]
    [InlineData(DedupeActionState.Quarantining)]
    [InlineData(DedupeActionState.Restoring)]
    public async Task Restart_flags_interrupted_intent_without_releasing_claims_or_touching_files(DedupeActionState state)
    {
        var plan = Plan();
        await System.IO.File.WriteAllTextAsync(plan.Files[0].Path,"keeper bytes");
        await System.IO.File.WriteAllTextAsync(plan.Files[1].Path,"copy bytes");
        await Store.TryPrepareAsync(plan,default);
        await using(var scope = _services.CreateAsyncScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            await db.DedupeActions.ExecuteUpdateAsync(s => s.SetProperty(a => a.State,state));
        }
        await Store.RecoverInterruptedAsync(default);
        await Store.RecoverInterruptedAsync(default);
        Assert.False(await Store.CancelPreparedAsync(plan.Id,default));
        Assert.Equal("keeper bytes",await System.IO.File.ReadAllTextAsync(plan.Files[0].Path));
        Assert.Equal("copy bytes",await System.IO.File.ReadAllTextAsync(plan.Files[1].Path));
        await using var check = _services.CreateAsyncScope();
        var context=check.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        Assert.Equal(DedupeActionState.NeedsAttention,(await context.DedupeActions.SingleAsync()).State);
        Assert.Equal(7,await context.DedupeFileClaims.CountAsync());
    }

    [Fact]
    public async Task Active_job_and_process_claims_prevent_preparation()
    {
        var plan=Plan();
        using(var claim=_services.GetRequiredService<FileOperationClaims>().TryAcquire(FileOperationClaims.SnapshotKeys(1,plan.Files[0].Path,plan.Files[0].Identity)))
            Assert.Equal(DedupePreparationResult.Conflict,await Store.TryPrepareAsync(plan,default));
        await using(var scope=_services.CreateAsyncScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            db.Libraries.Add(new() { Id=1,Name="Fixtures",Path=_root });
            db.MediaFiles.Add(new() { Id=1,LibraryId=1,Path=plan.Files[0].Path });
            db.Jobs.Add(new() { MediaFileId=1,LibraryId=1,Status=JobStatus.Queued });
            await db.SaveChangesAsync();
        }
        Assert.Equal(DedupePreparationResult.Conflict,await Store.TryPrepareAsync(plan,default));
        await using var check=_services.CreateAsyncScope();
        Assert.Empty(await check.ServiceProvider.GetRequiredService<OptimisarrDbContext>().DedupeActions.ToListAsync());
    }

    [Fact]
    public async Task Concurrent_overlapping_preparations_have_one_durable_winner()
    {
        var plan=Plan();
        var results=await Task.WhenAll(Enumerable.Range(0,12).Select(_ => Store.TryPrepareAsync(plan with { Id=Guid.NewGuid() },default)));
        Assert.Single(results, r => r == DedupePreparationResult.Created);
        Assert.All(results.Where(r => r != DedupePreparationResult.Created),r => Assert.Equal(DedupePreparationResult.Conflict,r));
    }
    [Fact]
    public async Task Recovery_rebuilds_missing_reservations_from_the_frozen_plan()
    {
        var plan=Plan();
        await Store.TryPrepareAsync(plan,default);
        await using(var scope=_services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>().DedupeFileClaims.ExecuteDeleteAsync();
        await Store.RecoverInterruptedAsync(default);
        await using var check=_services.CreateAsyncScope();
        Assert.Equal(7,await check.ServiceProvider.GetRequiredService<OptimisarrDbContext>().DedupeFileClaims.CountAsync());
        Assert.Equal(DedupePreparationResult.Conflict,await Store.TryPrepareAsync(plan with { Id=Guid.NewGuid() },default));
    }

    [Theory]
    [InlineData("contract")]
    [InlineData("retention")]
    [InlineData("path")]
    public async Task Corrupt_or_unsupported_rollback_evidence_blocks_startup_without_releasing_any_claim(string changed)
    {
        var plan=Plan();
        await Store.TryPrepareAsync(plan,default);
        await using(var scope=_services.CreateAsyncScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            if(changed == "contract") await db.DedupeActions.ExecuteUpdateAsync(s => s.SetProperty(a => a.Contract,"unsupported"));
            else if(changed == "retention") await db.DedupeActions.ExecuteUpdateAsync(s => s.SetProperty(a => a.Retention,"AutoPurge"));
            else await db.DedupeActionFiles.Where(f => !f.IsKeeper).ExecuteUpdateAsync(s => s.SetProperty(f => f.QuarantinePath,Path.Combine(_root,"tampered")));
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.RecoverInterruptedAsync(default));
        await using var check=_services.CreateAsyncScope();
        Assert.Equal(7,await check.ServiceProvider.GetRequiredService<OptimisarrDbContext>().DedupeFileClaims.CountAsync());
    }

    [Fact]
    public async Task Invalid_paths_roles_or_hashes_never_reserve_files_or_create_a_journal()
    {
        var plan=Plan();
        var invalid = new[]
        {
            plan with { Files=[plan.Files[0],plan.Files[1] with { Path=Path.Combine(_root,"..","outside") }] },
            plan with { Files=[plan.Files[0],plan.Files[1] with { QuarantinePath=plan.Files[0].Path }] },
            plan with { Files=[plan.Files[0],plan.Files[1] with { QuarantinePath=Path.GetPathRoot(_root) }] },
            plan with { Files=[plan.Files[0],plan.Files[1] with { Path=plan.Files[0].Path }] },
            plan with { Files=[plan.Files[0],plan.Files[1] with { Identity="" }] },
            plan with { ReviewKey="stale" }
        };
        foreach(var bad in invalid) Assert.Equal(DedupePreparationResult.Invalid,await Store.TryPrepareAsync(bad,default));
        await using var check=_services.CreateAsyncScope();
        var db=check.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        Assert.Empty(await db.DedupeFileClaims.ToListAsync());
        Assert.Empty(await db.DedupeActions.ToListAsync());
    }

    [Fact]
    public async Task Inventory_and_library_removal_cannot_cascade_away_rollback_history()
    {
        var plan=Plan();
        await using(var scope=_services.CreateAsyncScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            db.Libraries.Add(new() { Id=1,Name="Fixtures",Path=_root });
            db.MediaFiles.Add(new() { Id=1,LibraryId=1,Path=plan.Files[0].Path });
            await db.SaveChangesAsync();
        }
        await Store.TryPrepareAsync(plan,default);
        await using(var scope=_services.CreateAsyncScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            await db.MediaFiles.ExecuteDeleteAsync();
            await db.Libraries.ExecuteDeleteAsync();
            Assert.Equal(2,await db.DedupeActionFiles.CountAsync());
            Assert.Equal(7,await db.DedupeFileClaims.CountAsync());
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.DedupeActions.ExecuteDeleteAsync());
        }
        await Store.RecoverInterruptedAsync(default);
    }

}
