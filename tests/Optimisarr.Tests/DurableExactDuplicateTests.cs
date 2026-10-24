using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Optimisarr.Api.Library;
using Optimisarr.Data;

namespace Optimisarr.Tests;

public sealed class DurableExactDuplicateTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "optimisarr-durable-duplicates-" + Guid.NewGuid());
    private ServiceProvider _services = null!;
    private ExactDuplicateStore Store => _services.GetRequiredService<ExactDuplicateStore>();
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        var services = new ServiceCollection();
        services.AddDbContext<OptimisarrDbContext>(o => o.UseSqlite($"Data Source={Path.Combine(_root, "index.db")};Pooling=False"));
        services.AddSingleton<ExactDuplicateStore>();
        _services = services.BuildServiceProvider();
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        await db.Database.MigrateAsync();
        db.Libraries.Add(new() { Name = "Fixtures", Path = _root });
        await db.SaveChangesAsync();
    }
    public async Task DisposeAsync() { await _services.DisposeAsync(); Directory.Delete(_root, true); }

    [Fact]
    public async Task Completed_reports_survive_restart_and_unchanged_rescans_read_zero_bytes()
    {
        var inputs = new[] { await File(1, "a", [1, 2, 3]), await File(2, "b", [1, 2, 3]) };
        var first = await Run(inputs);
        Assert.Equal(6, first.Result!.BytesRead);
        var checkedAt = first.Result.Groups[0].Copies[0].CheckedAt;
        using var restarted = Coordinator();
        await restarted.StartAsync(default);
        var restored = await restarted.ReadAsync(1, 0, default);
        Assert.Equal("Completed", restored.Status);
        Assert.Single(restored.Result!.Groups);
        await restarted.StopAsync(default);
        var second = await Run(inputs);
        Assert.Equal(0, second.Result!.BytesRead);
        Assert.Equal(2, second.Result.Reused);
        Assert.Equal(checkedAt, second.Result.Groups[0].Copies[0].CheckedAt);
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        Assert.Equal(2, await db.ExactFileFingerprints.CountAsync());
        await db.Database.MigrateAsync();
        Assert.Equal(2, await db.ExactFileFingerprints.CountAsync());
    }

    [Fact]
    public async Task New_size_collision_backfills_a_previously_unique_file()
    {
        var a = await File(1, "a", [1, 2, 3]);
        var first = await Run([a]);
        Assert.Equal(0, first.Result!.Checked);
        var second = await Run([a, await File(2, "b", [1, 2, 3])]);
        Assert.Single(second.Result!.Groups);
        Assert.Equal(6, second.Result.BytesRead);
    }

    [Fact]
    public async Task Forced_refresh_detects_same_size_timestamp_preserving_edits()
    {
        var a = await File(1, "a", [1, 2, 3]); var b = await File(2, "b", [1, 2, 3]);
        await Run([a, b]);
        await System.IO.File.WriteAllBytesAsync(b.Path, [3, 2, 1]);
        System.IO.File.SetLastWriteTimeUtc(b.Path, b.ModifiedAt.UtcDateTime);
        var cached = await Run([a, b]);
        Assert.Equal(2, cached.Result!.Reused);
        Assert.Single(cached.Result.Groups);
        var refreshed = await Run([a, b], true);
        Assert.Empty(refreshed.Result!.Groups);
        Assert.Equal(6, refreshed.Result.BytesRead);
        Assert.Equal(0, refreshed.Result.Reused);
    }

    [Fact]
    public async Task Missing_file_invalidates_cache_and_cannot_join_a_group()
    {
        var a = await File(1, "a", [1, 2, 3]); var b = await File(2, "b", [1, 2, 3]);
        await Run([a, b]); System.IO.File.Delete(b.Path);
        var result = await Run([a, b]);
        Assert.Empty(result.Result!.Groups);
        Assert.Equal(1, result.Result.Skipped);
        await using var scope = _services.CreateAsyncScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>().ExactFileFingerprints.CountAsync());
    }

    [Fact]
    public async Task Startup_marks_abandoned_work_interrupted_without_publishing_partial_results()
    {
        var inputs = new[] { await File(1, "a", [1, 2, 3]), await File(2, "b", [1, 2, 3]) };
        Assert.NotNull(await Store.TryCreateAsync(1, _root, inputs.Length, false, default));
        using var service = Coordinator(); await service.StartAsync(default);
        var status = await service.ReadAsync(1, 0, default);
        Assert.Equal("Interrupted", status.Status);
        Assert.Null(status.Result);
        await service.StopAsync(default);
        Assert.Equal("Completed", (await Run(inputs)).Status);
    }

    [Fact]
    public async Task Results_are_paginated_without_losing_groups()
    {
        var inputs = new List<ExactDuplicateInput>();
        for (var i = 1; i <= 25; i++)
        {
            inputs.Add(await File(i * 2, $"{i}-a", Enumerable.Repeat((byte)i, i).ToArray()));
            inputs.Add(await File(i * 2 + 1, $"{i}-b", Enumerable.Repeat((byte)i, i).ToArray()));
        }
        var result = await Run(inputs);
        Assert.Equal(25, result.Result!.TotalGroups);
        Assert.Equal(20, result.Result.Groups.Count);
        Assert.True(result.Result.HasMore);
        var next = await Store.ReadAsync(1, 1, default);
        Assert.Equal(5, next.Result!.Groups.Count);
        Assert.False(next.Result.HasMore);
        Assert.Empty(result.Result.Groups.Select(g => g.Sha256).Intersect(next.Result.Groups.Select(g => g.Sha256)));
    }

    [Fact]
    public async Task Replacing_a_path_with_new_identity_invalidates_a_same_size_cached_hash()
    {
        var a = await File(1, "a", [1, 2, 3]); var b = await File(2, "b", [1, 2, 3]);
        await Run([a, b]);
        var replacement = Path.Combine(_root, "replacement");
        await System.IO.File.WriteAllBytesAsync(replacement, [3, 2, 1]);
        System.IO.File.SetLastWriteTimeUtc(replacement, b.ModifiedAt.UtcDateTime);
        System.IO.File.Move(replacement, b.Path, true);
        var result = await Run([a, b]);
        Assert.Empty(result.Result!.Groups);
        Assert.Equal(3, result.Result.BytesRead);
        Assert.Equal(1, result.Result.Reused);
    }

    [Fact]
    public async Task Adding_inventory_candidates_marks_the_saved_report_stale()
    {
        var a = await File(1, "a", [1, 2, 3]); var b = await File(2, "b", [1, 2, 3]);
        var first = await Run([a, b]); Assert.False(first.IsStale);
        await File(3, "c", [1, 2, 3]);
        Assert.True((await Store.ReadAsync(1, 0, default)).IsStale);
    }

    [Fact]
    public async Task Checkpoint_retries_are_idempotent_and_preserve_original_read_time()
    {
        var a = await File(1, "a", [1, 2, 3]); var b = await File(2, "b", [1, 2, 3]);
        var scanId = (await Store.TryCreateAsync(1, _root, 2, false, default))!.Value;
        var checkpoints = new List<ExactDuplicateCheckpoint>();
        await new ExactDuplicateScanner(TimeSpan.Zero).ScanAsync(_root, [a, b], _ => { }, default,
            checkpoint: (unit, _) => { checkpoints.Add(unit); return Task.CompletedTask; });
        await Store.CheckpointAsync(scanId, checkpoints, default);
        await Store.CheckpointAsync(scanId, checkpoints, default);
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        Assert.Equal(2, await db.ExactDuplicateObservations.CountAsync());
        Assert.Equal(2, await db.ExactFileFingerprints.CountAsync());
    }

    [Fact]
    public async Task Cancelled_work_keeps_only_completed_units_for_a_later_rescan()
    {
        var inputs = new[] { await File(1, "a", new byte[300_000]), await File(2, "b", new byte[300_000]) };
        using var service = new ExactDuplicateCoordinator(new ExactDuplicateScanner(TimeSpan.FromMilliseconds(100)), Store);
        await service.StartAsync(default);
        try
        {
            Assert.True(await service.TryStartAsync(1, _root, inputs, false, default));
            async Task WaitFor(Func<ExactDuplicateStatus, bool> ready)
            {
                // The coordinator allows ten seconds to flush completed checkpoints after a
                // cancellation. Loaded Windows CI can need that allowance; the old two-second
                // poll deadline could inspect the live status while that flush was still valid.
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                while (!ready(await service.ReadAsync(1, 0, deadline.Token))) await Task.Delay(10, deadline.Token);
            }
            await WaitFor(s => s.Progress.BytesRead > 300_000);
            Assert.True(service.Cancel(1));
            await WaitFor(s => s.Status is not ("Running" or "Queued"));
            var status = await service.ReadAsync(1, 0, default);
            Assert.Equal("Cancelled", status.Status); Assert.Null(status.Result);
            await using var scope = _services.CreateAsyncScope();
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>().ExactFileFingerprints.CountAsync());
        }
        finally { await service.StopAsync(default); }
        var next = await Run(inputs);
        Assert.Equal(1, next.Result!.Reused);
        Assert.Single(next.Result.Groups);
    }

    [Fact]
    public async Task Permission_failures_skip_files_and_invalidate_old_fingerprints()
    {
        if (OperatingSystem.IsWindows()) return;
        var a = await File(1, "a", [1, 2, 3]); var b = await File(2, "b", [1, 2, 3]);
        await Run([a, b]);
        var mode = System.IO.File.GetUnixFileMode(b.Path);
        try
        {
            System.IO.File.SetUnixFileMode(b.Path, UnixFileMode.None);
            var result = await Run([a, b]);
            Assert.Equal(1, result.Result!.Skipped);
            Assert.Empty(result.Result.Groups);
        }
        finally { System.IO.File.SetUnixFileMode(b.Path, mode); }
    }

    [Fact]
    public async Task A_one_file_change_rereads_only_that_file()
    {
        var a = await File(1, "a", [1, 2, 3]); var b = await File(2, "b", [1, 2, 3]);
        var c = await File(3, "c", [1, 2, 3, 4]); var d = await File(4, "d", [1, 2, 3, 4]);
        await Run([a, b, c, d]);
        await System.IO.File.WriteAllBytesAsync(b.Path, [3, 2, 1]);
        System.IO.File.SetLastWriteTimeUtc(b.Path, b.ModifiedAt.AddMinutes(1).UtcDateTime);
        b = b with { ModifiedAt = System.IO.File.GetLastWriteTimeUtc(b.Path) };
        var result = await Run([a, b, c, d]);
        Assert.Equal(3, result.Result!.Reused);
        Assert.Equal(3, result.Result.BytesRead);
        Assert.Single(result.Result.Groups);
    }

    [Fact]
    public async Task Deleting_the_library_during_a_scan_does_not_fault_the_background_service()
    {
        var inputs = new[] { await File(1, "a", [1, 2, 3]), await File(2, "b", [1, 2, 3]) };
        using var service = new ExactDuplicateCoordinator(new ExactDuplicateScanner(TimeSpan.FromMilliseconds(100)), Store);
        await service.StartAsync(default);
        try
        {
            Assert.True(await service.TryStartAsync(1, _root, inputs, false, default));
            for (var i = 0; i < 200 && (await service.ReadAsync(1, 0, default)).Progress.BytesRead == 0; i++) await Task.Delay(10);
            await using (var scope = _services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
                await db.Libraries.Where(l => l.Id == 1).ExecuteDeleteAsync();
            }
            for (var i = 0; i < 200 && (await service.ReadAsync(1, 0, default)).Status is "Running" or "Queued"; i++) await Task.Delay(10);
            Assert.False(service.ExecuteTask!.IsFaulted);
            Assert.Equal("NotStarted", (await service.ReadAsync(1, 0, default)).Status);
        }
        finally { await service.StopAsync(default); }
    }

    private ExactDuplicateCoordinator Coordinator() => new(new ExactDuplicateScanner(TimeSpan.Zero), Store);
    private async Task<ExactDuplicateStatus> Run(IReadOnlyList<ExactDuplicateInput> inputs, bool refresh = false)
    {
        using var service = Coordinator(); await service.StartAsync(default);
        try
        {
            Assert.True(await service.TryStartAsync(1, _root, inputs, refresh, default));
            for (var i = 0; i < 500; i++)
            {
                var status = await service.ReadAsync(1, 0, default);
                if (status.Status is not ("Running" or "Queued")) return status;
                await Task.Delay(20);
            }
            throw new TimeoutException("Fixture scan did not finish.");
        }
        finally { await service.StopAsync(default); }
    }
    private async Task<ExactDuplicateInput> File(int id, string name, byte[] bytes)
    {
        var path = Path.Combine(_root, name); await System.IO.File.WriteAllBytesAsync(path, bytes);
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        db.MediaFiles.Add(new() { Id = id, LibraryId = 1, Path = path, RelativePath = name,
            SizeBytes = bytes.Length, ModifiedAt = System.IO.File.GetLastWriteTimeUtc(path) });
        await db.SaveChangesAsync();
        return new(id, path, name, bytes.Length, System.IO.File.GetLastWriteTimeUtc(path));
    }
}
