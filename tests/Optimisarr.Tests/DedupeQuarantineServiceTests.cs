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
        services.AddHttpClient();
        services.AddScoped<Optimisarr.Api.Library.ArrTrackedFilesService>(provider => new Optimisarr.Api.Library.ArrTrackedFilesService(
            provider.GetRequiredService<OptimisarrDbContext>(), provider.GetRequiredService<IHttpClientFactory>(),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Optimisarr.Api.Library.ArrTrackedFilesService>>()));
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
    public async Task A_media_manager_that_cannot_be_asked_refuses_the_action()
    {
        var plan = await PreparedAsync();
        await InDb(async db =>
        {
            db.ArrConnections.Add(new ArrConnection { Name = "Radarr", BaseUrl = "http://localhost:9", Enabled = true });
            await db.SaveChangesAsync();
        });

        var result = await Quarantine(plan);

        Assert.Contains(new DedupePreflightIssue(null, DedupePreflightRefusal.ManagerUnavailable), result.Issues);
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
    public async Task Cancelling_as_the_action_starts_stops_it_and_keeps_the_cancellation()
    {
        var plan = await PreparedAsync();

        var result = await Service.QuarantineAsync(plan.Id, default, TimeSpan.Zero, beforeIntent: async () =>
            Assert.True(await Journal.CancelPreparedAsync(plan.Id, default)));

        Assert.Equal(DedupeActionOutcome.Refused, result.Outcome);
        AssertAllInPlace();
        Assert.Equal((DedupeActionState.Cancelled, 0), await ReadAsync(plan.Id));
    }

    [PosixFact]
    public async Task A_started_action_cannot_be_cancelled_out_from_under_its_moves()
    {
        var plan = await PreparedAsync();
        await Quarantine(plan, _ => { });

        Assert.False(await Journal.CancelPreparedAsync(plan.Id, default));

        var (state, claims) = await ReadAsync(plan.Id);
        Assert.Equal(DedupeActionState.Quarantined, state);
        Assert.True(claims > 0);
    }

    [PosixFact]
    public async Task A_keeper_rewritten_with_its_size_and_time_restored_stops_the_action()
    {
        var plan = await PreparedAsync();
        var modified = File.GetLastWriteTimeUtc(Keeper);
        var rewritten = false;

        var result = await Quarantine(plan, _ =>
        {
            if (rewritten) return;
            Thread.Sleep(50);
            File.WriteAllBytes(Keeper, [9, 9, 9, 9, 9]);
            File.SetLastWriteTimeUtc(Keeper, modified);
            rewritten = true;
        });

        Assert.Equal(DedupeActionOutcome.Refused, result.Outcome);
        Assert.Equal(Content, await File.ReadAllBytesAsync(CopyA));
        Assert.Equal(Content, await File.ReadAllBytesAsync(CopyB));
    }

    [PosixFact]
    public async Task A_keeper_lost_after_the_last_move_undoes_the_action()
    {
        var plan = await PreparedAsync();
        var moves = 0;

        var result = await Quarantine(plan, _ => { if (++moves == 2) File.Delete(Keeper); });

        Assert.Equal(DedupeActionOutcome.Refused, result.Outcome);
        Assert.Equal(Content, await File.ReadAllBytesAsync(CopyA));
        Assert.Equal(Content, await File.ReadAllBytesAsync(CopyB));
    }

    [PosixFact]
    public async Task A_quarantine_folder_redirected_between_moves_undoes_the_action()
    {
        var plan = await PreparedAsync();
        var elsewhere = Directory.CreateTempSubdirectory("optimisarr-dedupe-elsewhere-").FullName;
        try
        {
            var folder = Path.GetDirectoryName(plan.Files[2].QuarantinePath!)!;
            var redirected = false;
            var result = await Quarantine(plan, _ =>
            {
                if (redirected) return;
                Directory.CreateDirectory(Path.GetDirectoryName(folder)!);
                Directory.CreateSymbolicLink(folder, elsewhere);
                redirected = true;
            });

            Assert.Equal(DedupeActionOutcome.Refused, result.Outcome);
            Assert.Empty(Directory.EnumerateFileSystemEntries(elsewhere));
            Assert.Equal(Content, await File.ReadAllBytesAsync(CopyA));
            Assert.Equal(Content, await File.ReadAllBytesAsync(CopyB));
        }
        finally { Directory.Delete(elsewhere, true); }
    }

    [PosixFact]
    public async Task A_keepers_folder_redirected_between_moves_undoes_the_action()
    {
        var keeperFolder = Path.Combine(_root, "Keeper");
        Directory.CreateDirectory(keeperFolder);
        var keeper = Path.Combine(keeperFolder, "Film.mkv");
        File.Move(Keeper, keeper);
        var id = Guid.NewGuid();
        var plan = new DedupePreparation(id, 1, new('a', 64), new('b', 64),
            [Snapshot(1, keeper, id, true), Snapshot(2, CopyA, id, false), Snapshot(3, CopyB, id, false)]);
        Assert.Equal(DedupePreparationResult.Created, await Journal.TryPrepareAsync(plan, default));
        var outside = Directory.CreateTempSubdirectory("optimisarr-dedupe-elsewhere-").FullName;
        try
        {
            var moved = Path.Combine(outside, "Keeper");
            var redirected = false;
            var result = await Quarantine(plan, _ =>
            {
                if (redirected) return;
                Directory.Move(keeperFolder, moved);
                Directory.CreateSymbolicLink(keeperFolder, moved);
                redirected = true;
            });

            Assert.Equal(DedupeActionOutcome.Refused, result.Outcome);
            Assert.Equal(Content, await File.ReadAllBytesAsync(CopyA));
            Assert.Equal(Content, await File.ReadAllBytesAsync(CopyB));
        }
        finally { Directory.Delete(outside, true); }
    }

    [PosixFact]
    public async Task Cancelling_after_the_last_move_still_returns_what_moved()
    {
        var plan = await PreparedAsync();
        using var cancel = new CancellationTokenSource();
        var moves = 0;

        var result = await Service.QuarantineAsync(plan.Id, cancel.Token, TimeSpan.Zero, _ => { if (++moves == 2) cancel.Cancel(); });

        Assert.Equal(2, moves);
        Assert.Equal(DedupeActionOutcome.Refused, result.Outcome);
        AssertAllInPlace();
        Assert.Equal((DedupeActionState.Restored, 0), await ReadAsync(plan.Id));
    }

    [PosixFact]
    public async Task Restore_refuses_an_original_folder_redirected_outside_the_library()
    {
        var plan = await PreparedAsync();
        await Quarantine(plan);
        var elsewhere = Directory.CreateTempSubdirectory("optimisarr-dedupe-elsewhere-").FullName;
        try
        {
            var folder = Path.GetDirectoryName(CopyA)!;
            Directory.Delete(folder);
            Directory.CreateSymbolicLink(folder, elsewhere);

            var result = await Service.RestoreAsync(plan.Id, default);

            Assert.Equal(DedupeActionOutcome.NeedsAttention, result.Outcome);
            Assert.Empty(Directory.EnumerateFileSystemEntries(elsewhere));
            Assert.Equal(Content, await File.ReadAllBytesAsync(plan.Files[1].QuarantinePath!));
            Assert.True((await ReadAsync(plan.Id)).Claims > 0);
        }
        finally { Directory.Delete(elsewhere, true); }
    }

    [PosixFact]
    public async Task A_quarantined_copy_rewritten_with_its_size_and_time_restored_is_not_restored()
    {
        var plan = await PreparedAsync();
        await Quarantine(plan);
        var quarantined = plan.Files[1].QuarantinePath!;
        var modified = File.GetLastWriteTimeUtc(quarantined);
        await File.WriteAllBytesAsync(quarantined, [9, 9, 9, 9, 9]);
        File.SetLastWriteTimeUtc(quarantined, modified);

        var result = await Service.RestoreAsync(plan.Id, default);

        Assert.Equal(DedupeActionOutcome.NeedsAttention, result.Outcome);
        Assert.False(File.Exists(CopyA));
        Assert.True((await ReadAsync(plan.Id)).Claims > 0);
    }

    [PosixFact]
    public async Task Cancelling_part_way_returns_what_moved_and_finishes_the_journal()
    {
        var plan = await PreparedAsync();
        using var cancel = new CancellationTokenSource();

        var result = await Service.QuarantineAsync(plan.Id, cancel.Token, TimeSpan.Zero, _ => cancel.Cancel());

        Assert.Equal(DedupeActionOutcome.Refused, result.Outcome);
        AssertAllInPlace();
        Assert.Equal((DedupeActionState.Restored, 0), await ReadAsync(plan.Id));
    }

    [PosixFact]
    public async Task An_action_that_stopped_part_way_can_be_restored_without_a_restart()
    {
        var plan = await PreparedAsync();
        await Assert.ThrowsAsync<StopAtBoundary>(() => Quarantine(plan, _ => throw new StopAtBoundary()));

        Assert.Equal(DedupeActionOutcome.Completed, (await Service.RestoreAsync(plan.Id, default)).Outcome);

        AssertAllInPlace();
        Assert.Equal((DedupeActionState.Restored, 0), await ReadAsync(plan.Id));
    }

    [PosixFact]
    public async Task A_destination_outside_the_actions_folder_is_refused_before_anything_moves()
    {
        var id = Guid.NewGuid();
        var plan = new DedupePreparation(id, 1, new('a', 64), new('b', 64),
        [
            Snapshot(1, Keeper, id, true),
            Snapshot(2, CopyA, id, false) with { QuarantinePath = Path.Combine(_root, "ordinary-folder", "Film.mkv") },
            Snapshot(3, CopyB, id, false),
        ]);
        Assert.Equal(DedupePreparationResult.Created, await Journal.TryPrepareAsync(plan, default));

        var result = await Quarantine(plan);

        Assert.Equal(DedupeActionOutcome.Refused, result.Outcome);
        AssertAllInPlace();
        Assert.False(Directory.Exists(Path.Combine(_root, "ordinary-folder")));
    }

    [PosixFact]
    public async Task Cleaning_up_a_refusal_leaves_a_similarly_named_sibling_folder()
    {
        var sibling = Path.Combine(_root, DedupeQuarantinePaths.FolderName + "-other");
        Directory.CreateDirectory(sibling);
        var plan = await PreparedAsync();
        await File.WriteAllBytesAsync(Keeper, [9, 9, 9, 9, 9]);

        await Quarantine(plan);

        Assert.True(Directory.Exists(sibling));
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
