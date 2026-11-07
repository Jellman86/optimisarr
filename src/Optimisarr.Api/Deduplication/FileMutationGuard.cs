using Optimisarr.Data;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Core.Deduplication;

namespace Optimisarr.Api.Deduplication;

/// <summary>Fences a bounded filesystem mutation while the caller owns the SQLite writer.</summary>
internal static class FileMutationGuard
{
    /// <param name="targets">What the caller will delete. Copies at rest in quarantine only let cleanup go
    /// ahead when none of these overlaps a folder or file their action recorded.</param>
    public static async Task<IDisposable?> TryAcquireRecursiveCleanupAsync(
        OptimisarrDbContext db, FileOperationClaims claims, CancellationToken token, params string[] targets)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Recursive cleanup requires a writer transaction before checking reservations.");
        // Opaque path/identity keys cannot establish that an entire tree is unrelated to an
        // absent destination or an aliased parent. Until reference capture is qualified, defer
        // recursive cleanup globally rather than infer safety from a directory's own key. Copies at
        // rest in quarantine are the exception: they wait for weeks, and holding every scratch folder
        // on the server for that long would stall previews and quality searches. Their action recorded
        // every path it owns, so cleanup goes ahead only when its own targets, followed through links,
        // cannot reach any of them. Any action moving files still defers cleanup.
        var held = claims.TryAcquireRecursiveCleanup();
        if (held is null) return null;
        try
        {
            var moving = await db.DedupeActions.AsNoTracking().AnyAsync(
                a => a.State != DedupeActionState.Restored && a.State != DedupeActionState.Cancelled
                    && a.State != DedupeActionState.Purged && a.State != DedupeActionState.Quarantined, token);
            var orphaned = await db.DedupeFileClaims.AsNoTracking().AnyAsync(claim => !db.DedupeActions.Any(
                action => action.Id == claim.ActionId && action.State == DedupeActionState.Quarantined), token);
            if (!moving && !orphaned && !await ReachesRestingCopiesAsync(db, targets, token)) return held;
            held.Dispose();
            return null;
        }
        catch
        {
            held.Dispose();
            throw;
        }
    }

    private static async Task<bool> ReachesRestingCopiesAsync(OptimisarrDbContext db, string[] targets, CancellationToken token)
    {
        var owned = await db.DedupeActionFiles.AsNoTracking()
            .Where(file => db.DedupeActions.Any(action => action.Id == file.ActionId && action.State == DedupeActionState.Quarantined))
            .Select(file => new { file.Root, file.Path, file.QuarantinePath })
            .ToListAsync(token);
        if (owned.Count == 0) return false;
        if (targets.Length == 0) return true;
        var protectedPaths = owned.SelectMany(file => new[] { file.Root, file.Path, file.QuarantinePath })
            .OfType<string>().Distinct().SelectMany(Forms).ToList();
        return targets.SelectMany(Forms).Any(target => protectedPaths.Any(path => Overlaps(target, path)));
    }

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    // A path as written and as reached through any link on the way, since either can lead into a library.
    private static IEnumerable<string> Forms(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        yield return full;
        var real = RealPath(full);
        if (!string.Equals(real, full, PathComparison)) yield return real;
    }

    private static string RealPath(string full)
    {
        var current = Path.GetPathRoot(full)!;
        foreach (var part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            try
            {
                FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
                if (info.Exists && info.ResolveLinkTarget(returnFinalTarget: true) is { } target) current = Path.GetFullPath(target.FullName);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return Path.TrimEndingDirectorySeparator(current);
    }

    private static bool Overlaps(string first, string second) =>
        string.Equals(first, second, PathComparison) || Inside(first, second) || Inside(second, first);

    private static bool Inside(string child, string parent) =>
        child.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar, PathComparison);

    public static async Task<IDisposable?> TryAcquireAsync(
        OptimisarrDbContext db, FileOperationClaims claims, int mediaFileId,
        string[] paths, CancellationToken token)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("File mutations require a writer transaction before checking reservations.");
        var keys = FileOperationClaims.Keys(mediaFileId, paths);
        var held = claims.TryAcquire(keys);
        if (held is null) return null;
        try
        {
            if (!await DedupeJournalStore.IsReservedAsync(db, keys, token)) return held;
            held.Dispose();
            return null;
        }
        catch
        {
            held.Dispose();
            throw;
        }
    }
}
