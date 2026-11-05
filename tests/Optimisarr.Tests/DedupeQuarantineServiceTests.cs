using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Optimisarr.Api.Deduplication;
using Optimisarr.Api.Library;
using Optimisarr.Core.Deduplication;
using Optimisarr.Data;

namespace Optimisarr.Tests;

public sealed class DedupeQuarantineServiceTests : IAsyncLifetime
{
    private sealed class StopAtBoundary : Exception;

    private static readonly byte[] Content = [1, 2, 3, 4, 5];
    private readonly string _root = Path.GetFullPath(Directory.CreateTempSubdirectory("optimisarr-dedupe-action-").FullName);
    private ServiceProvider _services = null!;
    private DedupeQuarantineService Service => _services.GetRequiredService<DedupeQuarantineService>();
    private DedupeJournalStore Journal => _services.GetRequiredService<DedupeJournalStore>();
    private string Keeper => Path.Combine(_root, "Film.mkv");
    private string CopyA => Path.Combine(_root, "A", "Film.mkv");
    private string CopyB => Path.Combine(_root, "B", "Film.mkv");

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddDbContext<OptimisarrDbContext>(o => o.UseSqlite($"Data Source={Path.Combine(_root, "journal.db")};Pooling=False"));
        services.AddSingleton<FileOperationClaims>();
        services.AddSingleton<DedupeJournalStore>();
        services.AddSingleton<DedupeQuarantineService>();
        _services = services.BuildServiceProvider();
        await InDb(db => db.Database.MigrateAsync());
        foreach (var path in new[] { Keeper, CopyA, CopyB })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, Content);
        }
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        Directory.Delete(_root, true);
    }

    private DedupeFileSnapshot Snapshot(int id, string path, Guid action, bool keeper)
    {
        var info = new FileInfo(path);
        return new(id, 1, _root, path, keeper ? null : DedupeQuarantinePaths.For(_root, action, path),
            ExactFileIdentity.Read(path)!, info.Length, info.LastWriteTimeUtc.Ticks, Convert.ToHexStringLower(SHA256.HashData(Content)), keeper);
    }

    private async Task<DedupePreparation> PreparedAsync()
    {
        var id = Guid.NewGuid();
        var plan = new DedupePreparation(id, 1, new('a', 64), new('b', 64),
            [Snapshot(1, Keeper, id, true), Snapshot(2, CopyA, id, false), Snapshot(3, CopyB, id, false)]);
        Assert.Equal(DedupePreparationResult.Created, await Journal.TryPrepareAsync(plan, default));
        return plan;
    }

    private Task<DedupeActionResult> Quarantine(DedupePreparation plan, Action<DedupeFileSnapshot>? afterMove = null) =>
        Service.QuarantineAsync(plan.Id, default, TimeSpan.Zero, afterMove);

    private async Task InDb(Func<OptimisarrDbContext, Task> action)
    {
        await using var scope = _services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>());
    }

    private async Task<(DedupeActionState State, int Claims)> ReadAsync(Guid id)
    {
        (DedupeActionState, int) result = default;
        await InDb(async db => result = ((await db.DedupeActions.AsNoTracking().SingleAsync(a => a.Id == id)).State,
            await db.DedupeFileClaims.CountAsync(c => c.ActionId == id)));
        return result;
    }

    private void AssertAllInPlace()
    {
        foreach (var path in new[] { Keeper, CopyA, CopyB }) Assert.Equal(Content, File.ReadAllBytes(path));
    }

    [PosixFact]
    public async Task Copies_move_to_quarantine_keeping_the_same_files_and_the_keeper_stays()
    {
        var plan = await PreparedAsync();
        var identity = plan.Files[1].Identity;

        var result = await Quarantine(plan);

        Assert.Equal(DedupeActionOutcome.Completed, result.Outcome);
        Assert.Equal(Content, await File.ReadAllBytesAsync(Keeper));
        Assert.False(File.Exists(CopyA));
        Assert.False(File.Exists(CopyB));
        Assert.Equal(identity, ExactFileIdentity.Read(plan.Files[1].QuarantinePath!));
        var (state, claims) = await ReadAsync(plan.Id);
        Assert.Equal(DedupeActionState.Quarantined, state);
        Assert.True(claims > 0);
    }

    [PosixFact]
    public async Task Restore_returns_every_copy_to_its_exact_path_and_releases_reservations()
    {
        var plan = await PreparedAsync();
        await Quarantine(plan);
        var identity = plan.Files[1].Identity;

        var result = await Service.RestoreAsync(plan.Id, default);

        Assert.Equal(DedupeActionOutcome.Completed, result.Outcome);
        AssertAllInPlace();
        Assert.Equal(identity, ExactFileIdentity.Read(CopyA));
        Assert.Equal((DedupeActionState.Restored, 0), await ReadAsync(plan.Id));
        Assert.False(Directory.Exists(Path.Combine(_root, DedupeQuarantinePaths.FolderName)));
    }

    [PosixFact]
    public async Task Repeating_a_finished_action_changes_nothing()
    {
        var plan = await PreparedAsync();
        await Quarantine(plan);
        Assert.Equal(DedupeActionOutcome.Completed, (await Quarantine(plan)).Outcome);
        Assert.Equal(DedupeActionState.Quarantined, (await ReadAsync(plan.Id)).State);

        await Service.RestoreAsync(plan.Id, default);
        Assert.Equal(DedupeActionOutcome.Completed, (await Service.RestoreAsync(plan.Id, default)).Outcome);
        AssertAllInPlace();
    }

    [PosixFact]
    public async Task Preflight_refusal_moves_nothing_and_leaves_the_plan_prepared()
    {
        var plan = await PreparedAsync();
        var modified = File.GetLastWriteTimeUtc(Keeper);
        await File.WriteAllBytesAsync(Keeper, [9, 9, 9, 9, 9]);
        File.SetLastWriteTimeUtc(Keeper, modified);

        var result = await Quarantine(plan);

        Assert.Equal(DedupeActionOutcome.Refused, result.Outcome);
        Assert.Contains(new DedupePreflightIssue(1, DedupePreflightRefusal.ContentDiffers), result.Issues);
        Assert.True(File.Exists(CopyA));
        Assert.True(File.Exists(CopyB));
        Assert.Equal(DedupeActionState.Prepared, (await ReadAsync(plan.Id)).State);
    }

    [PosixFact]
    public async Task An_enabled_media_manager_refuses_the_action()
    {
        var plan = await PreparedAsync();
        await InDb(async db =>
        {
            db.ArrConnections.Add(new ArrConnection { Name = "Radarr", BaseUrl = "http://radarr", Enabled = true });
            await db.SaveChangesAsync();
        });

        var result = await Quarantine(plan);

        Assert.Contains(new DedupePreflightIssue(null, DedupePreflightRefusal.ManagerConnected), result.Issues);
        AssertAllInPlace();
    }

    [PosixFact]
    public async Task A_keeper_lost_between_moves_stops_the_action_and_returns_what_moved()
    {
        var plan = await PreparedAsync();
        var kept = Keeper + ".aside";

        var result = await Quarantine(plan, _ => { if (File.Exists(Keeper)) File.Move(Keeper, kept); });

        Assert.Equal(DedupeActionOutcome.Refused, result.Outcome);
        Assert.Equal(Content, await File.ReadAllBytesAsync(CopyA));
        Assert.Equal(Content, await File.ReadAllBytesAsync(CopyB));
        Assert.Equal((DedupeActionState.Restored, 0), await ReadAsync(plan.Id));
    }

    [PosixFact]
    public async Task Stopping_after_a_move_is_recovered_from_where_the_files_are()
    {
        var plan = await PreparedAsync();

        await Assert.ThrowsAsync<StopAtBoundary>(() => Quarantine(plan, _ => throw new StopAtBoundary()));
        Assert.False(File.Exists(CopyA));
        Assert.True(File.Exists(CopyB));
        Assert.Equal(DedupeActionState.Quarantining, (await ReadAsync(plan.Id)).State);

        await Journal.RecoverInterruptedAsync(default);
        Assert.Equal(DedupeActionState.NeedsAttention, (await ReadAsync(plan.Id)).State);

        Assert.Equal(DedupeActionOutcome.Completed, (await Service.RestoreAsync(plan.Id, default)).Outcome);
        AssertAllInPlace();
        Assert.Equal((DedupeActionState.Restored, 0), await ReadAsync(plan.Id));
    }

    [PosixFact]
    public async Task An_occupied_original_path_is_never_overwritten_and_keeps_the_reservations()
    {
        var plan = await PreparedAsync();
        await Quarantine(plan);
        await File.WriteAllTextAsync(CopyA, "a new file arrived here");

        var result = await Service.RestoreAsync(plan.Id, default);

        Assert.Equal(DedupeActionOutcome.NeedsAttention, result.Outcome);
        Assert.Equal("a new file arrived here", await File.ReadAllTextAsync(CopyA));
        Assert.Equal(Content, await File.ReadAllBytesAsync(plan.Files[1].QuarantinePath!));
        var (state, claims) = await ReadAsync(plan.Id);
        Assert.Equal(DedupeActionState.NeedsAttention, state);
        Assert.True(claims > 0);

        File.Delete(CopyA);
        Assert.Equal(DedupeActionOutcome.Completed, (await Service.RestoreAsync(plan.Id, default)).Outcome);
        AssertAllInPlace();
    }

    [PosixFact]
    public async Task A_quarantined_file_that_changed_is_not_restored_as_the_reviewed_copy()
    {
        var plan = await PreparedAsync();
        await Quarantine(plan);
        await File.WriteAllTextAsync(plan.Files[1].QuarantinePath!, "changed in quarantine");

        var result = await Service.RestoreAsync(plan.Id, default);

        Assert.Equal(DedupeActionOutcome.NeedsAttention, result.Outcome);
        Assert.False(File.Exists(CopyA));
        Assert.Equal("changed in quarantine", await File.ReadAllTextAsync(plan.Files[1].QuarantinePath!));
    }

    [PosixFact]
    public async Task Another_operation_holding_the_files_refuses_without_changing_anything()
    {
        var plan = await PreparedAsync();
        using var held = _services.GetRequiredService<FileOperationClaims>().TryAcquire(DedupeJournalStore.Keys(plan.Files));

        var result = await Quarantine(plan);

        Assert.Equal(DedupeActionOutcome.Refused, result.Outcome);
        AssertAllInPlace();
        Assert.Equal(DedupeActionState.Prepared, (await ReadAsync(plan.Id)).State);
    }

    [Fact]
    public async Task An_unknown_action_is_reported_as_not_found() =>
        Assert.Equal(DedupeActionOutcome.NotFound, (await Service.QuarantineAsync(Guid.NewGuid(), default)).Outcome);
}
