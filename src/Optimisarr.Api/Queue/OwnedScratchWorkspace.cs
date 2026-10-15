using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Deduplication;
using Optimisarr.Api.Library;
using Optimisarr.Core.Queue;
using Optimisarr.Data;

namespace Optimisarr.Api.Queue;

/// <summary>Owns explicit stopped-process outputs; never recursively deletes an unknown tree.</summary>
internal sealed class OwnedScratchWorkspace : IAsyncDisposable
{
    private readonly IServiceScopeFactory _scopes;
    private readonly FileOperationClaims _claims;
    private readonly string _workRoot;
    private readonly int _jobId;
    private readonly int _attempt;
    private readonly OwnerReference _owner;
    private readonly ILogger _logger;
    private readonly string _marker;
    private readonly string _nonce;
    private readonly FileGeneration _markerGeneration;
    private readonly Dictionary<string, FileGeneration?> _files = new(OperatingSystem.IsLinux()
        ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _captured = new(OperatingSystem.IsLinux()
        ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

    public string Root { get; }

    private OwnedScratchWorkspace(IServiceScopeFactory scopes, FileOperationClaims claims, string workRoot,
        int jobId, int attempt, Job owner, string root, string marker, string nonce, FileGeneration markerGeneration, ILogger logger)
    {
        _scopes = scopes; _claims = claims; _workRoot = workRoot; _jobId = jobId; _attempt = attempt;
        _owner = new(owner.MediaFileId, owner.LibraryId, owner.Type, owner.CalibrationSessionId);
        Root = root; _marker = marker; _nonce = nonce; _markerGeneration = markerGeneration; _logger = logger;
    }

    public static async Task<OwnedScratchWorkspace> CreateAsync(
        IServiceScopeFactory scopes, FileOperationClaims claims, string workRoot, int jobId, int attempt,
        string parent, string purpose, ILogger logger, CancellationToken token)
    {
        if (purpose is not ("adaptive-quality" or "verification-reference")) throw new ArgumentException("Unknown scratch purpose.", nameof(purpose));
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        await using var write = await db.BeginDiagnosticWriteAsync(token);
        var job = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == jobId, token);
        if (job is null || job.ExecutionAttempt != attempt
            || job.Status is not (JobStatus.Probing or JobStatus.Transcoding or JobStatus.Verifying))
            throw new FileCleanupDeferredException();
        var ownerRoot = job.Type switch
        {
            JobType.Preview => WorkOutputRoot.ForPreview(workRoot, job.Id),
            JobType.Calibration when job.CalibrationSessionId is { } session => WorkOutputRoot.ForCalibration(workRoot, session, job.Id),
            JobType.Normal => WorkOutputRoot.ForMediaFile(workRoot, job.MediaFileId),
            _ => throw new FileCleanupDeferredException()
        };
        parent = Path.GetFullPath(parent);
        if (!SamePath(parent, ownerRoot) && !WorkPaths.IsUnderRoot(ownerRoot, parent)) throw new FileCleanupDeferredException();
        using var held = await FileMutationGuard.TryAcquireRecursiveCleanupAsync(db, claims, token);
        if (held is null) throw new FileCleanupDeferredException();
        var libraries = await db.Libraries.AsNoTracking().Select(l => l.Path).ToListAsync(token);
        if (libraries.Any(p => SamePath(p, parent) || WorkPaths.IsUnderRoot(p, parent))) throw new FileCleanupDeferredException();
        // Directory creation is bounded and guarded too. The configured work root's parents may
        // be operator-selected system aliases; redirects inside it are refused.
        Directory.CreateDirectory(workRoot);
        var existing = parent;
        while (!Directory.Exists(existing)) existing = Path.GetDirectoryName(existing) ?? throw new FileCleanupDeferredException();
        if (!ExactDuplicateScanner.SafePath(workRoot, existing)) throw new FileCleanupDeferredException();
        var nonce = Guid.NewGuid().ToString("N");
        var root = Path.Combine(parent, $".{purpose}-{jobId}-{nonce}");
        if (File.Exists(root) || Directory.Exists(root)) throw new FileCleanupDeferredException();
        Directory.CreateDirectory(root);
        var marker = Path.Combine(root, ".optimisarr-owner");
        using (var stream = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream)) writer.Write(nonce);
        var generation = FileGeneration.Read(marker) ?? throw new FileCleanupDeferredException();
        await write.CommitAsync(token);
        return new(scopes, claims, workRoot, jobId, attempt, job, root, marker, nonce, generation, logger);
    }

    /// <summary>Called after process exit/reaping, before any measurement that could observe a changed file.</summary>
    public void Capture(string path)
    {
        path = Path.GetFullPath(path);
        if (!SamePath(Path.GetDirectoryName(path)!, Root) || SamePath(path, _marker))
            throw new InvalidOperationException("Scratch outputs must be direct children of their owned workspace.");
        // A second callback must not adopt replacement bytes under an already captured name.
        if (_captured.Add(path)) _files.Add(path, FileGeneration.Read(path));
    }

    public async Task<bool> TryDeleteAsync(string path)
    {
        path = Path.GetFullPath(path);
        if (!_files.TryGetValue(path, out var generation) || generation is null) return false;
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            await using var write = await db.BeginDiagnosticWriteAsync();
            var job = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == _jobId, CancellationToken.None);
            if (!OwnerMatches(job)) return false;
            var deleted = await QueueOutputCleanup.TryDiscardScratchAsync(db, _claims, job!, path, _workRoot,
                () => MarkerMatches() && (FileGeneration.Read(path) is { } current ? current == generation : Missing(path)), default);
            await write.CommitAsync();
            if (deleted) _files.Remove(path);
            return deleted;
        }
        catch (Exception ex) when (ex is System.Data.Common.DbException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Retained scratch file for job {JobId} attempt {Attempt} at {Path}", _jobId, _attempt, path);
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            // A blocked guard or unavailable writer should not multiply database waits across
            // every sample in the search. Keep the remaining workspace for later cleanup.
            foreach (var path in _files.Keys.ToArray())
                if (!await TryDeleteAsync(path)) return;
            await using var scope = _scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            await using var write = await db.BeginDiagnosticWriteAsync();
            var job = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == _jobId, CancellationToken.None);
            if (!OwnerMatches(job) || _files.Count != 0 || !MarkerMatches()) return;
            // Only the marker may remain. Foreign files, nested folders and changed generations
            // retain the workspace rather than broadening the scope of this disposal.
            if (Directory.EnumerateFileSystemEntries(Root).Any(p => !SamePath(p, _marker))) return;
            if (!await QueueOutputCleanup.TryDiscardScratchAsync(db, _claims, job!, _marker, _workRoot, MarkerMatches, default)) return;
            using var held = await FileMutationGuard.TryAcquireRecursiveCleanupAsync(db, _claims, default);
            if (held is null) return;
            Directory.Delete(Root, recursive: false);
            await write.CommitAsync();
        }
        catch (Exception ex) when (ex is System.Data.Common.DbException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Retained scratch for job {JobId} attempt {Attempt} at {Path}", _jobId, _attempt, Root);
        }
        finally
        {
            if (Directory.Exists(Root)) _logger.LogWarning("Scratch cleanup deferred for job {JobId} attempt {Attempt} at {Path}", _jobId, _attempt, Root);
        }
    }

    private sealed record OwnerReference(int MediaFileId, int? LibraryId, JobType Type, Guid? CalibrationSessionId);

    private bool OwnerMatches(Job? job) => job is not null && job.ExecutionAttempt == _attempt
        && _owner == new OwnerReference(job.MediaFileId, job.LibraryId, job.Type, job.CalibrationSessionId);

    private bool MarkerMatches() => _markerGeneration == FileGeneration.Read(_marker)
        && ExactDuplicateScanner.SafePath(_workRoot, _marker) && File.ReadAllText(_marker) == _nonce;

    private static bool Missing(string path)
    {
        try { File.GetAttributes(path); return false; }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return true; }
    }

    private static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    private sealed record FileGeneration(string Identity, long Length, long ModifiedTicks)
    {
        private static readonly FileGeneration Absent = new("", -1, -1);
        public static FileGeneration? Read(string path)
        {
            try
            {
                if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) return null;
                var info = new FileInfo(path);
                return ExactFileIdentity.Read(path) is { } identity ? new(identity, info.Length, info.LastWriteTimeUtc.Ticks) : null;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return Absent; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }
    }
}
