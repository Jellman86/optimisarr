using Optimisarr.Data;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Core.Deduplication;

namespace Optimisarr.Api.Deduplication;

/// <summary>Fences a bounded filesystem mutation while the caller owns the SQLite writer.</summary>
internal static class FileMutationGuard
{
    public static async Task<IDisposable?> TryAcquireRecursiveCleanupAsync(
        OptimisarrDbContext db, FileOperationClaims claims, CancellationToken token)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Recursive cleanup requires a writer transaction before checking reservations.");
        // Opaque path/identity keys cannot establish that an entire tree is unrelated to an
        // absent destination or an aliased parent. Until reference capture is qualified, defer
        // recursive cleanup globally rather than infer safety from a directory's own key. Copies at
        // rest in quarantine are the exception: they wait for weeks, and holding every scratch folder
        // on the server for that long would stall previews and quality searches. They sit in their
        // library's own folder, and these trees are scratch under the work root, whose owners refuse
        // any overlap with a library. Any action actually moving files still defers cleanup.
        var held = claims.TryAcquireRecursiveCleanup();
        if (held is null) return null;
        try
        {
            var unresolved = await db.DedupeActions.AsNoTracking().AnyAsync(
                a => a.State != DedupeActionState.Restored && a.State != DedupeActionState.Cancelled
                    && a.State != DedupeActionState.Purged && a.State != DedupeActionState.Quarantined, token);
            var claimed = await db.DedupeFileClaims.AsNoTracking().AnyAsync(claim => !db.DedupeActions.Any(
                action => action.Id == claim.ActionId && action.State == DedupeActionState.Quarantined), token);
            if (!unresolved && !claimed) return held;
            held.Dispose();
            return null;
        }
        catch
        {
            held.Dispose();
            throw;
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
