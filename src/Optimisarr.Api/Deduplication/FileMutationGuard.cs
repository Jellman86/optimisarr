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
        // recursive cleanup globally rather than infer safety from a directory's own key.
        var held = claims.TryAcquireRecursiveCleanup();
        if (held is null) return null;
        try
        {
            var unresolved = await db.DedupeActions.AsNoTracking().AnyAsync(
                a => a.State != DedupeActionState.Restored && a.State != DedupeActionState.Cancelled, token);
            if (!unresolved && !await db.DedupeFileClaims.AsNoTracking().AnyAsync(token)) return held;
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
