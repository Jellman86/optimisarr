using Optimisarr.Data;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Library;
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
        // every path it owns, so cleanup goes ahead only when it can prove, by the filesystem's own
        // identities, that its targets cannot reach them: no target or folder above it is one of their
        // library folders, and nothing inside a target is one of those folders or files. Paths alone
        // cannot prove that, because a link or a bind mount can make one folder appear in two places.
        // Any action moving files still defers cleanup.
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

        // A library folder that cannot be identified cannot be proved out of reach.
        var folders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in owned.Select(file => file.Root).Distinct())
        {
            if (ExactFileIdentity.Read(root) is not { } identity) return true;
            folders.Add(identity);
        }
        var files = owned.SelectMany(file => new[] { file.Path, file.QuarantinePath }).OfType<string>()
            .Select(ExactFileIdentity.Read).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var all = folders.Concat(files).ToHashSet(StringComparer.Ordinal);
        return targets.Any(target => InsideAnyOf(target, folders) || Contains(target, all));
    }

    // The target, or the nearest folder above it that exists, and every folder above that.
    private static bool InsideAnyOf(string target, HashSet<string> folders)
    {
        for (var current = Path.GetFullPath(target); current is not null; current = Path.GetDirectoryName(current))
        {
            if (!Path.Exists(current)) continue;
            if (ExactFileIdentity.Read(current) is not { } identity) return true;
            if (folders.Contains(identity)) return true;
        }
        return false;
    }

    // Anything inside the target, as a recursive delete would meet it. Links are not followed, as a
    // recursive delete removes the link and not what it points to.
    private static bool Contains(string target, HashSet<string> protectedIdentities)
    {
        if (!Directory.Exists(target)) return false;
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
            foreach (var entry in Directory.EnumerateFileSystemEntries(target, "*", options))
            {
                if (ExactFileIdentity.Read(entry) is not { } identity || protectedIdentities.Contains(identity)) return true;
            }
            return false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

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
