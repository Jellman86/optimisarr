using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Optimisarr.Api.Library;
using Optimisarr.Data;
using System.Runtime.InteropServices;

namespace Optimisarr.Tests;

public sealed class CrossLibraryExactDuplicateTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "optimisarr-cross-copies-" + Guid.NewGuid());
    private ServiceProvider _services = null!;
    private ExactDuplicateStore Store => _services.GetRequiredService<ExactDuplicateStore>();
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        var services = new ServiceCollection();
        services.AddDbContext<OptimisarrDbContext>(o => o.UseSqlite($"Data Source={Path.Combine(_root, "db.sqlite")};Pooling=False"));
        services.AddSingleton<ExactDuplicateStore>(); _services = services.BuildServiceProvider();
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        await db.Database.MigrateAsync();
        for (var id = 1; id <= 3; id++)
        {
            var root = Path.Combine(_root, id.ToString()); Directory.CreateDirectory(root);
            db.Libraries.Add(new() { Id = id, Name = $"Library {id}", Path = root });
        }
        await db.SaveChangesAsync();
    }
    public async Task DisposeAsync() { await _services.DisposeAsync(); Directory.Delete(_root, true); }

    [Fact]
    public async Task Upgrading_a_D1_snapshot_preserves_evidence_but_requires_rescan_before_preferences()
    {
        await File(1, 1, "one"); await File(2, 1, "two");
        await Run([1]);
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        await db.GetService<IMigrator>().MigrateAsync("20261011162829_AddDurableExactCopies");
        await db.Database.MigrateAsync();
        await db.Database.MigrateAsync();
        var selected = Assert.Single(await db.ExactDuplicateScanLibraries.AsNoTracking().ToListAsync());
        Assert.Equal(1, selected.LibraryId); Assert.Equal(Path.Combine(_root, "1"), selected.Root);
        var observations = await db.ExactDuplicateObservations.AsNoTracking().ToListAsync();
        Assert.Equal(2, observations.Count);
        Assert.All(observations, row => { Assert.Equal(1, row.LibraryId); Assert.Equal(selected.Root, row.Root); Assert.Null(row.Identity); });
        var old = await Store.ReadAsync(1, 0, default);
        Assert.True(old.IsStale); Assert.False(Assert.Single(old.Result!.Groups).CanDecide);
        var refreshed = Assert.Single((await Run([1])).Result!.Groups);
        Assert.True(refreshed.CanDecide);
        Assert.True(await Store.DecideAsync(1, refreshed.ReviewKey!, "Keep", [1], default));
    }

    [Fact]
    public async Task Explicit_scope_finds_cross_library_size_collisions_and_excludes_unselected_libraries()
    {
        await File(1, 1, "same.mkv"); await File(2, 2, "same.mkv"); await File(3, 3, "same.mkv");
        var single = await Run([1]); Assert.Empty(single.Result!.Groups);
        var combined = await Run([1, 2]); var group = Assert.Single(combined.Result!.Groups);
        Assert.Equal(new[] { 1, 2 }, group.Copies.Select(c => c.LibraryId));
        Assert.Equal(new[] { 1, 2 }, combined.Libraries!.Select(l => l.Id));
        Assert.Equal(6, combined.Progress.BytesRead);
        Assert.True(group.CanDecide);
    }

    [Fact]
    public async Task Hardlinks_across_library_roots_are_one_copy_with_explicit_alias_ownership()
    {
        var first = await File(1, 1, "one"); var alias = await File(2, 2, "linked");
        System.IO.File.Delete(alias);
        Assert.True(OperatingSystem.IsWindows() ? CreateHardLink(alias, first, IntPtr.Zero) : Link(first, alias) == 0);
        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            (await db.MediaFiles.SingleAsync(f => f.Id == 2)).ModifiedAt = System.IO.File.GetLastWriteTimeUtc(alias);
            await db.SaveChangesAsync();
        }
        await File(3, 1, "independent");
        var group = Assert.Single((await Run([1, 2])).Result!.Groups);
        Assert.Equal(2, group.TotalCopies); Assert.Null(group.ExtraCopyBytes);
        var reference = Assert.Single(group.Copies.Single(c => c.Id == 1).Aliases!);
        Assert.Equal(2, reference.LibraryId); Assert.Equal(2, reference.Id);
        Assert.False(await Store.DecideAsync(1, group.ReviewKey!, "Keep", [2], default));
        Assert.True(await Store.DecideAsync(1, group.ReviewKey!, "Keep", [1], default));
    }

    [Fact]
    public async Task Keep_multiple_and_ignore_survive_restart_and_retries_do_not_rewrite_preferences()
    {
        await File(1, 1, "one"); await File(2, 2, "two");
        var first = Assert.Single((await Run([1, 2])).Result!.Groups);
        Assert.True(await Store.DecideAsync(1, first.ReviewKey!, "Keep", [1, 2], default));
        var saved = Assert.Single((await Store.ReadAsync(1, 0, default)).Result!.Groups).Decision!;
        Assert.Equal(new[] { 1, 2 }, saved.KeepIds);
        Assert.True(await Store.DecideAsync(1, first.ReviewKey!, "Keep", [2, 1], default));
        Assert.Equal(saved.SavedAt, Assert.Single((await Store.ReadAsync(1, 0, default)).Result!.Groups).Decision!.SavedAt);
        await Store.RecoverAsync(default);
        var second = Assert.Single((await Run([1, 2])).Result!.Groups);
        Assert.Equal("Keep", second.Decision!.Choice);
        Assert.True(await Store.DecideAsync(1, second.ReviewKey!, "Ignore", [], default));
        Assert.Equal("Ignore", Assert.Single((await Run([1, 2])).Result!.Groups).Decision!.Choice);
        Assert.True(await Store.DecideAsync(1, second.ReviewKey!, "Review", [], default));
        Assert.Null(Assert.Single((await Store.ReadAsync(1, 0, default)).Result!.Groups).Decision);
    }

    [Fact]
    public async Task Changed_generations_and_changed_scope_invalidate_saved_preferences()
    {
        await File(1, 1, "one"); var b = await File(2, 2, "two"); await File(3, 3, "three");
        var original = Assert.Single((await Run([1, 2])).Result!.Groups);
        Assert.True(await Store.DecideAsync(1, original.ReviewKey!, "Ignore", [], default));
        var widened = Assert.Single((await Run([1, 2, 3])).Result!.Groups);
        Assert.Null(widened.Decision); Assert.NotEqual(original.ReviewKey, widened.ReviewKey);
        await Run([1, 2]);
        System.IO.File.SetLastWriteTimeUtc(b, System.IO.File.GetLastWriteTimeUtc(b).AddSeconds(5));
        var stale = await Store.ReadAsync(1, 0, default);
        Assert.True(stale.IsStale); Assert.False(Assert.Single(stale.Result!.Groups).CanDecide);
        Assert.False(await Store.DecideAsync(1, original.ReviewKey!, "Keep", [1], default));
        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            (await db.MediaFiles.SingleAsync(f => f.Id == 2)).ModifiedAt = System.IO.File.GetLastWriteTimeUtc(b); await db.SaveChangesAsync();
        }
        var refreshed = Assert.Single((await Run([1, 2])).Result!.Groups);
        Assert.Null(refreshed.Decision); Assert.NotEqual(original.ReviewKey, refreshed.ReviewKey);
    }

    [Fact]
    public async Task A_deleted_secondary_library_does_not_silently_shrink_the_report_scope()
    {
        await File(1, 1, "one"); await File(2, 2, "two"); await Run([1, 2]);
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        await db.Libraries.Where(l => l.Id == 2).ExecuteDeleteAsync();
        var status = await Store.ReadAsync(1, 0, default);
        Assert.True(status.IsStale); Assert.Equal(2, status.Libraries!.Count);
        Assert.False(Assert.Single(status.Result!.Groups).CanDecide);
        await db.Database.MigrateAsync();
    }

    [Fact]
    public async Task Unknown_keys_outside_keepers_and_unselected_inputs_cannot_save_preferences()
    {
        await File(1, 1, "one"); await File(2, 2, "two");
        var group = Assert.Single((await Run([1, 2])).Result!.Groups);
        Assert.False(await Store.DecideAsync(2, group.ReviewKey!, "Keep", [1], default));
        Assert.False(await Store.DecideAsync(1, group.ReviewKey!, "Keep", [3], default));
        Assert.False(await Store.DecideAsync(1, new('a', 64), "Ignore", [], default));
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var outside = await ExactDuplicateInputs.ReadAsync(db, new[] { 1, 2 }, default);
        Assert.Null(await Store.TryCreateAsync(1, Path.Combine(_root, "1"), 2, false, default, outside));
    }

    [DllImport("libc", EntryPoint = "link", SetLastError = true)] private static extern int Link(string source, string target);
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateHardLink(string target, string source, IntPtr reserved);

    private async Task<string> File(int id, int libraryId, string name)
    {
        var path = Path.Combine(_root, libraryId.ToString(), name); await System.IO.File.WriteAllBytesAsync(path, [1, 2, 3]);
        await using var scope = _services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        db.MediaFiles.Add(new() { Id = id, LibraryId = libraryId, Path = path, RelativePath = name, SizeBytes = 3, ModifiedAt = System.IO.File.GetLastWriteTimeUtc(path) });
        await db.SaveChangesAsync(); return path;
    }
    private async Task<ExactDuplicateStatus> Run(int[] ids)
    {
        await using var scope = _services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var selected = await db.Libraries.AsNoTracking().Where(l => ids.Contains(l.Id)).Select(l => new ExactDuplicateLibrary(l.Id, l.Name, l.Path, l.MediaType.ToString())).ToListAsync();
        var inputs = await ExactDuplicateInputs.ReadAsync(db, ids, default);
        using var service = new ExactDuplicateCoordinator(new ExactDuplicateScanner(TimeSpan.Zero), Store); await service.StartAsync(default);
        try
        {
            Assert.True(await service.TryStartAsync(1, Path.Combine(_root, "1"), inputs, false, default, selected));
            for (var i = 0; i < 250; i++)
            {
                var status = await service.ReadAsync(1, 0, default);
                if (status.Status is not ("Running" or "Queued")) return status;
                await Task.Delay(20);
            }
            throw new TimeoutException();
        }
        finally { await service.StopAsync(default); }
    }
}
