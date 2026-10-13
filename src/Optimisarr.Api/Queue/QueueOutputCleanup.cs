using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Deduplication;
using Optimisarr.Api.Library;
using Optimisarr.Core.Workers;
using Optimisarr.Data;

namespace Optimisarr.Api.Queue;

/// <summary>Discards queue-owned scratch without detaching reserved files from their diagnostics.</summary>
internal static class QueueOutputCleanup
{
    public static async Task<bool> TryDiscardAsync(
        OptimisarrDbContext db, FileOperationClaims claims, Job job, string workRoot,
        Func<string, bool> isDirectoryReserved, CancellationToken token)
    {
        // The caller must retain the writer through its job transition and commit. This also
        // fences independent coordinators, which do not share the in-memory claims registry.
        using var held = await FileMutationGuard.TryAcquireRecursiveCleanupAsync(db, claims, token);
        if (held is null) return false;
        if (await db.JobLeases.AsNoTracking().AnyAsync(l => l.JobId == job.Id && l.State == LeaseState.Held, token)
            || await db.Replacements.AsNoTracking().AnyAsync(r => r.JobId == job.Id
                && JobClearing.LiveReplacementStatuses.Contains(r.Status), token)) return false;

        var path = job.WorkOutputPath;
        // Completed move-to-folder outputs are retained when their queue history is cleared.
        if (string.IsNullOrEmpty(path) || !WorkPaths.IsUnderRoot(workRoot, path)) return true;

        var otherOutputs = await db.Jobs.AsNoTracking().Where(j => j.Id != job.Id && j.WorkOutputPath != null)
            .Select(j => j.WorkOutputPath!).ToListAsync(token);
        var recoveryPaths = await db.Replacements.AsNoTracking()
            .Where(r => r.Status != ReplacementStatus.Purged && r.Status != ReplacementStatus.RolledBack)
            .Select(r => new { r.OriginalPath, r.QuarantinePath, r.FinalPath }).ToListAsync(token);
        var uploads = await db.JobLeases.AsNoTracking().Where(l => l.State == LeaseState.Held && l.ResultUploadPath != null)
            .Select(l => l.ResultUploadPath!).ToListAsync(token);
        var protectedPaths = otherOutputs.Concat(uploads).Concat(recoveryPaths.SelectMany(r =>
            new[] { r.OriginalPath, r.QuarantinePath, r.FinalPath })).ToArray();
        var libraryRoots = await db.Libraries.AsNoTracking().Select(l => l.Path).ToListAsync(token);
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var canonical = Path.GetFullPath(path);
        if (isDirectoryReserved(Path.GetDirectoryName(canonical)!)) return false;
        if (protectedPaths.Any(p => string.Equals(Path.GetFullPath(p), canonical, comparison))
            || libraryRoots.Any(root => string.Equals(Path.GetFullPath(root), canonical, comparison)
                || WorkPaths.IsUnderRoot(root, canonical))) return false;

        // Source rows can outlive a library root change. Protect these even if the work root
        // was mistakenly configured as a media folder, without hashing the library's contents.
        var sourcePath = await db.MediaFiles.AsNoTracking().Where(f => f.Id == job.MediaFileId)
            .Select(f => f.Path).SingleOrDefaultAsync(token);
        if (sourcePath is not null)
        {
            if (string.Equals(Path.GetFullPath(sourcePath), canonical, comparison)) return false;
            var sourceIdentity = ExactFileIdentity.Read(sourcePath);
            if (sourceIdentity is not null && sourceIdentity == ExactFileIdentity.Read(canonical)) return false;
        }
        if (await db.MediaFiles.AsNoTracking().AnyAsync(f => EF.Functions.Collate(f.Path,
            comparison == StringComparison.Ordinal ? "BINARY" : "NOCASE") == canonical, token)) return false;

        token.ThrowIfCancellationRequested();
        try
        {
            // Inspect the nearest existing ancestor too: File.Exists alone treats a dangling
            // symlink as absent and would permit pruning through a redirected work directory.
            var existing = canonical;
            while (true)
            {
                try { File.GetAttributes(existing); break; }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                existing = Path.GetDirectoryName(existing) ?? throw new IOException("No work ancestor exists.");
            }
            if (!ExactDuplicateScanner.SafePath(workRoot, existing) || Directory.Exists(canonical)) return false;
            if (File.Exists(canonical)) File.Delete(canonical);
            WorkPaths.PruneEmptyAncestors(workRoot, canonical, directory =>
                isDirectoryReserved(directory) || protectedPaths.Any(p => WorkPaths.IsUnderRoot(directory, p)));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
