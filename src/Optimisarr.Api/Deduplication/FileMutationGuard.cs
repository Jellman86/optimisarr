using Optimisarr.Data;

namespace Optimisarr.Api.Deduplication;

/// <summary>Fences a bounded filesystem mutation while the caller owns the SQLite writer.</summary>
internal static class FileMutationGuard
{
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
