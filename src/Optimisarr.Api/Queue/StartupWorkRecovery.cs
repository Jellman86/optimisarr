using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Deduplication;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Workers;
using Optimisarr.Data;

namespace Optimisarr.Api.Queue;

internal sealed record StartupRecoveryResult(bool Complete, bool Changed);

/// <summary>Retries a fixed startup plan, never a fresh sweep of disposable work.</summary>
internal sealed class StartupWorkRecovery(
    IServiceScopeFactory scopeFactory, string workRoot, FileOperationClaims claims,
    ILogger logger, Func<string, bool> directoryInUse)
{
    private sealed record Snapshot(int MediaId, int? LibraryId, JobType Type, JobStatus Status,
        int Attempt, int ExecutionAttempt, DateTimeOffset Enqueued, DateTimeOffset Updated,
        string? Output, long? OutputSize, Guid? Session, string? Source, string? Error, double Progress);
    private Dictionary<int, Snapshot>? _pending;
    private Dictionary<string, DateTime>? _orphans;
    private static readonly StringComparison PathComparison = OperatingSystem.IsLinux()
        ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    public bool BlocksDispatch(int jobId) => _pending?.ContainsKey(jobId) == true;

    public async Task<StartupRecoveryResult> RunAsync(CancellationToken token)
    {
        if (_pending is { Count: 0 } && _orphans is { Count: 0 }) return new(true, false);
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        await using var write = await db.BeginDiagnosticWriteAsync(token);
        var jobs = await db.Jobs.AsNoTracking().ToListAsync(token);
        var sources = await db.MediaFiles.AsNoTracking().ToDictionaryAsync(f => f.Id, f => f.Path, token);
        var leased = (await db.JobLeases.AsNoTracking().Where(l => l.State != LeaseState.Released
            && l.State != LeaseState.Expired && l.State != LeaseState.Completed)
            .Select(l => l.JobId).ToListAsync(token)).ToHashSet();
        _pending ??= jobs.Where(IsStartupTarget).ToDictionary(j => j.Id, j => Capture(j, sources));
        // Work owned at capture cannot become an orphan target just because a newer operation
        // clears its reference before this delayed pass finally acquires the cleanup claim.
        var ownedAtCapture = jobs.Where(j => OwnsScratch(j, leased)).Select(j => j.MediaFileId).ToHashSet();
        _orphans ??= WorkPaths.FindStaleOrphanDirectories(workRoot, ownedAtCapture, DateTime.UtcNow.AddDays(-7))
            .ToDictionary(p => p, Directory.GetLastWriteTimeUtc);
        using var held = await FileMutationGuard.TryAcquireRecursiveCleanupAsync(db, claims, token);
        if (held is null) return new(false, false);

        var libraries = await db.Libraries.AsNoTracking().Select(l => l.Path).ToListAsync(token);
        var replacements = await db.Replacements.AsNoTracking().ToListAsync(token);
        var retainedPaths = sources.Values.Concat(replacements.SelectMany(r => new[]
            { r.OriginalPath, r.QuarantinePath, r.FinalPath })).Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
        var handled = new List<int>();
        var changed = false;
        foreach (var (id, snapshot) in _pending)
        {
            token.ThrowIfCancellationRequested();
            var job = jobs.Find(j => j.Id == id);
            if (job is null || Capture(job, sources) != snapshot)
            {
                handled.Add(id);
                continue;
            }
            if (leased.Contains(id)) continue;
            // Successful calibration cleanup detaches retained diagnostics from the session.
            // A later restart has no tree to infer or remove from this audit-only row.
            if (job.Type == JobType.Calibration && job.CalibrationSessionId is null
                && job.WorkOutputPath is null && DiagnosticJobRetention.ShouldRetain(job.Type, job.Status))
            {
                handled.Add(id);
                continue;
            }
            var disposable = job.Type is JobType.Preview or JobType.Calibration;
            var tree = job.Type == JobType.Preview ? WorkOutputRoot.ForPreview(workRoot, id)
                : job.CalibrationSessionId is { } session
                    ? WorkOutputRoot.ForCalibration(workRoot, session, id) : null;
            var target = disposable ? tree : job.WorkOutputPath;
            if (disposable && (tree is null || job.WorkOutputPath is { } output && !Contains(tree, output))) continue;
            if (target is not null && (retainedPaths.Any(p => Overlaps(target, p))
                || libraries.Any(p => Overlaps(target, p))
                || jobs.Any(j => j.Id != id && j.WorkOutputPath is { } p && Overlaps(target, p)))) continue;
            var action = QueueDispatcher.RecoveryActionFor(job.Status, job.WorkOutputPath, job.Attempt);
            if (disposable ? !RemoveTree(tree!)
                : action != QueueDispatcher.RecoveryAction.KeepDelivered && !RemoveOutput(target)) continue;

            db.Jobs.Attach(job);
            if (disposable)
            {
                if (DiagnosticJobRetention.ShouldRetain(job.Type, job.Status))
                {
                    job.WorkOutputPath = null;
                    job.OutputSizeBytes = null;
                    job.CalibrationSessionId = null;
                }
                else db.Jobs.Remove(job);
            }
            else if (job.Status == JobStatus.Cancelled)
            {
                job.WorkOutputPath = null;
                job.OutputSizeBytes = null;
                job.UpdatedAt = DateTimeOffset.UtcNow;
            }
            else
            {
                job.Progress = 0;
                job.UpdatedAt = DateTimeOffset.UtcNow;
                if (action == QueueDispatcher.RecoveryAction.KeepDelivered) job.Status = JobStatus.AwaitingVerification;
                else
                {
                    job.WorkOutputPath = null;
                    job.OutputSizeBytes = null;
                    job.Status = action == QueueDispatcher.RecoveryAction.Fail ? JobStatus.Failed : JobStatus.Queued;
                    if (job.Status == JobStatus.Failed)
                    {
                        job.ErrorMessage = "Interrupted too many times (worker restarted while running).";
                        job.FinishedAt = DateTimeOffset.UtcNow;
                    }
                }
            }
            handled.Add(id);
            changed = true;
        }

        var retiredOrphans = new List<string>();
        foreach (var (directory, modified) in _orphans)
        {
            token.ThrowIfCancellationRequested();
            var mediaId = int.Parse(Path.GetFileName(directory));
            // Even queued work with no output yet owns its media scratch tree. Unknown states
            // and independent held leases must not be mistaken for terminal, ownerless work.
            var owned = jobs.Any(j => j.MediaFileId == mediaId && OwnsScratch(j, leased))
                || jobs.Any(j => j.WorkOutputPath is { } p && Overlaps(directory, p))
                || retainedPaths.Any(p => Overlaps(directory, p)) || libraries.Any(p => Overlaps(directory, p));
            if (owned || !Directory.Exists(directory) || Directory.GetLastWriteTimeUtc(directory) != modified)
                retiredOrphans.Add(directory);
            else if (RemoveTree(directory)) retiredOrphans.Add(directory);
        }
        if (changed) await db.SaveChangesAsync(token);
        await write.CommitAsync(token);
        foreach (var id in handled) _pending.Remove(id);
        foreach (var path in retiredOrphans) _orphans.Remove(path);
        return new(_pending.Count == 0 && _orphans.Count == 0, changed);
    }

    private static bool IsStartupTarget(Job j) => j.Type is JobType.Preview or JobType.Calibration
        || j.Type == JobType.Normal && (j.Status is JobStatus.Probing or JobStatus.Transcoding or JobStatus.Verifying
            || j.Status == JobStatus.Cancelled && j.WorkOutputPath != null);

    private static bool OwnsScratch(Job job, HashSet<int> leased) => job.WorkOutputPath is not null
        || job.Type is not (JobType.Normal or JobType.Preview or JobType.Calibration)
        || job.Status is not (JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled)
        || leased.Contains(job.Id);

    private static Snapshot Capture(Job j, Dictionary<int, string> sources) => new(j.MediaFileId, j.LibraryId,
        j.Type, j.Status, j.Attempt, j.ExecutionAttempt, j.EnqueuedAt, j.UpdatedAt, j.WorkOutputPath,
        j.OutputSizeBytes, j.CalibrationSessionId, sources.GetValueOrDefault(j.MediaFileId), j.ErrorMessage, j.Progress);

    private static bool Contains(string root, string path) => string.Equals(Path.GetFullPath(root), Path.GetFullPath(path), PathComparison)
        || WorkPaths.IsUnderRoot(root, path);
    private static bool Overlaps(string a, string b) => Contains(a, b) || Contains(b, a);

    private bool RemoveTree(string path) => Remove(path, recursive: true);
    private bool RemoveOutput(string? path) => path is null || Remove(path, recursive: false);

    private bool Remove(string path, bool recursive)
    {
        if (!WorkPaths.IsUnderRoot(workRoot, path)) return false;
        try
        {
            if (!AncestorsAreSafe(path)) return false;
            FileAttributes attributes;
            try { attributes = File.GetAttributes(path); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return true; }
            if ((attributes & FileAttributes.ReparsePoint) != 0) return false;
            if (recursive)
            {
                if ((attributes & FileAttributes.Directory) == 0 || !TreeIsSafe(path)) return false;
                Directory.Delete(path, recursive: true);
            }
            else
            {
                if ((attributes & FileAttributes.Directory) != 0) return false;
                File.Delete(path);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Startup cleanup retained work for retry at {Path}", path);
            return false;
        }
    }

    private bool AncestorsAreSafe(string path)
    {
        for (var parent = Path.GetDirectoryName(Path.GetFullPath(path)); parent is not null
            && Contains(workRoot, parent); parent = Path.GetDirectoryName(parent))
        {
            if (directoryInUse(parent)) return false;
            if (Directory.Exists(parent) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) return false;
        }
        return true;
    }

    private bool TreeIsSafe(string directory)
    {
        if (directoryInUse(directory)) return false;
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) return false;
            if ((attributes & FileAttributes.Directory) != 0 && !TreeIsSafe(path)) return false;
        }
        return true;
    }
}
