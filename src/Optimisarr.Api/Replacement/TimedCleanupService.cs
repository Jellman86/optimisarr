using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using Optimisarr.Api.Library;
using Optimisarr.Api.Queue;
using Optimisarr.Core.Replacement;
using Optimisarr.Data;

namespace Optimisarr.Api.Replacement;

/// <summary>Current files eligible for the shared cleanup policy.</summary>
public sealed record TimedCleanupPreview(
    int RetentionDays,
    bool DryRunMode,
    int FailedOutputCount,
    long FailedOutputBytes,
    int QuarantinedOriginalCount,
    long QuarantinedOriginalBytes,
    string PlanToken)
{
    public int TotalCount => FailedOutputCount + QuarantinedOriginalCount;
    public long TotalBytes => FailedOutputBytes + QuarantinedOriginalBytes;
}

/// <summary>The plan evaluated immediately before cleanup and what was actually reclaimed.</summary>
public sealed record TimedCleanupRunResult(
    TimedCleanupPreview Preview,
    int CleanedCount,
    long ReclaimedBytes);

/// <summary>A confirmed run is accepted only while its browser preview remains current.</summary>
public sealed record TimedCleanupAttempt(
    TimedCleanupPreview CurrentPreview,
    TimedCleanupRunResult? Result);

/// <summary>
/// Applies the configured retention window to space that Optimisarr deliberately keeps for later
/// review: quarantined originals and failed outputs under <c>/work</c>. Quarantined originals lose
/// their rollback path when purged; failed jobs keep their diagnostic row, report and FFmpeg log.
/// A retention of zero keeps both indefinitely.
/// </summary>
public sealed class TimedCleanupService
{
    private readonly OptimisarrDbContext _db;
    private readonly SettingsStore _settings;
    private readonly string _workRoot;
    private readonly ILogger<TimedCleanupService> _logger;

    public TimedCleanupService(
        OptimisarrDbContext db,
        SettingsStore settings,
        IHostEnvironment environment,
        ILogger<TimedCleanupService> logger)
        : this(db, settings, WorkPaths.Resolve(environment), logger)
    {
    }

    internal TimedCleanupService(
        OptimisarrDbContext db,
        SettingsStore settings,
        string workRoot,
        ILogger<TimedCleanupService> logger)
    {
        _db = db;
        _settings = settings;
        _workRoot = workRoot;
        _logger = logger;
    }

    /// <summary>Previews expired retained files without changing disk or database state.</summary>
    public async Task<TimedCleanupPreview> PreviewExpiredAsync(CancellationToken cancellationToken)
    {
        var queueSettings = await _settings.GetQueueSettingsAsync(cancellationToken);
        var plan = await BuildCleanupPlanAsync(queueSettings, DateTimeOffset.UtcNow, cancellationToken);
        return plan.Preview;
    }

    /// <summary>Purges all expired retained files and returns the total number processed.</summary>
    public async Task<int> PurgeExpiredAsync(CancellationToken cancellationToken)
    {
        var result = await RunExpiredAsync(cancellationToken);
        return result.CleanedCount;
    }

    /// <summary>
    /// Re-evaluates the current cleanup plan, applies it, and reports actual reclaimed bytes.
    /// Callers can safely use the returned preview in an audit trail because it is captured in
    /// the same service operation rather than trusting an earlier browser preview.
    /// </summary>
    public async Task<TimedCleanupRunResult> RunExpiredAsync(CancellationToken cancellationToken)
    {
        var queueSettings = await _settings.GetQueueSettingsAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var plan = await BuildCleanupPlanAsync(queueSettings, now, cancellationToken);
        return await ApplyCleanupPlanAsync(plan, now, cancellationToken);
    }

    /// <summary>
    /// Runs only when the exact policy, counts and measured bytes match the preview the operator
    /// confirmed. A changed plan is returned without touching disk or database state.
    /// </summary>
    public async Task<TimedCleanupAttempt> RunConfirmedExpiredAsync(
        TimedCleanupPreview confirmedPreview,
        CancellationToken cancellationToken)
    {
        var queueSettings = await _settings.GetQueueSettingsAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var plan = await BuildCleanupPlanAsync(queueSettings, now, cancellationToken);
        if (plan.Preview != confirmedPreview)
        {
            return new TimedCleanupAttempt(plan.Preview, null);
        }

        var result = await ApplyCleanupPlanAsync(plan, now, cancellationToken);
        return new TimedCleanupAttempt(plan.Preview, result);
    }

    private async Task<TimedCleanupRunResult> ApplyCleanupPlanAsync(
        CleanupPlan plan,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (plan.Preview.RetentionDays <= 0)
        {
            return new TimedCleanupRunResult(plan.Preview, 0, 0);
        }

        var quarantined = await PurgeExpiredQuarantinedOriginalsAsync(
            plan.QuarantinedOriginals, plan.Preview.RetentionDays, now, cancellationToken);
        var failedOutputs = await PurgeExpiredFailedOutputsAsync(
            plan.FailedOutputs, plan.Preview.RetentionDays, now, cancellationToken);
        return new TimedCleanupRunResult(
            plan.Preview,
            quarantined.Count + failedOutputs.Count,
            quarantined.Bytes + failedOutputs.Bytes);
    }

    private async Task<CleanupPlan> BuildCleanupPlanAsync(
        QueueSettings queueSettings,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var retentionDays = queueSettings.ReplacementQuarantineRetentionDays;
        if (retentionDays <= 0)
        {
            return CleanupPlan.Empty(retentionDays, queueSettings.DryRunMode);
        }

        var quarantinedOriginals = new List<Optimisarr.Data.Replacement>();
        if (!queueSettings.DryRunMode)
        {
            var replaced = await _db.Replacements
                .Where(replacement => replacement.Status == ReplacementStatus.Replaced)
                .ToListAsync(cancellationToken);
            var entries = replaced
                .Select(replacement => new QuarantineEntry(replacement.Id, replacement.ReplacedAt))
                .ToList();
            var expiredIds = QuarantineRetentionEvaluator
                .FindExpired(entries, retentionDays, now)
                .ToHashSet();
            quarantinedOriginals = replaced
                .Where(replacement => expiredIds.Contains(replacement.Id))
                .ToList();
        }

        // SQLite cannot reliably translate every DateTimeOffset comparison. The failed set is
        // bounded by queue history, so select the narrow candidate set and apply the cutoff here.
        var failed = await _db.Jobs
            .Where(job => job.Type == JobType.Normal
                && job.Status == JobStatus.Failed
                && job.WorkOutputPath != null
                && job.FinishedAt != null)
            .ToListAsync(cancellationToken);
        var cutoff = now.AddDays(-retentionDays);

        // A retry normally reuses the same row, but this guard prevents cleanup from deleting a
        // path that any concurrently active or ready job happens to reference.
        var protectedPaths = (await _db.Jobs
                .Where(job => job.WorkOutputPath != null
                    && (job.Status == JobStatus.Queued
                        || job.Status == JobStatus.Probing
                        || job.Status == JobStatus.Transcoding
                        || job.Status == JobStatus.Verifying
                        || job.Status == JobStatus.ReadyToReplace))
                .Select(job => job.WorkOutputPath!)
                .ToListAsync(cancellationToken))
            .ToHashSet(PathComparer);

        var failedOutputs = new List<Job>();
        foreach (var job in failed.Where(job => job.FinishedAt <= cutoff))
        {
            var path = job.WorkOutputPath!;
            if (protectedPaths.Contains(path))
            {
                continue;
            }
            if (!WorkPaths.IsUnderRoot(_workRoot, path))
            {
                _logger.LogWarning("Refused to inspect failed output outside the work root: {Path}", path);
                continue;
            }
            if (WasWrittenAfter(path, cutoff.UtcDateTime))
            {
                continue;
            }

            failedOutputs.Add(job);
        }

        var failedFiles = failedOutputs
            .Select(job => new CleanupFile("failed", job.Id, job.WorkOutputPath!, TryGetFileLength(job.WorkOutputPath!)))
            .ToList();
        var quarantinedFiles = quarantinedOriginals
            .Select(replacement => new CleanupFile(
                "quarantine", replacement.Id, replacement.QuarantinePath, TryGetFileLength(replacement.QuarantinePath)))
            .ToList();
        var preview = new TimedCleanupPreview(
            retentionDays,
            queueSettings.DryRunMode,
            failedOutputs.Count,
            failedFiles.Sum(file => file.Bytes),
            quarantinedOriginals.Count,
            quarantinedFiles.Sum(file => file.Bytes),
            BuildPlanToken(retentionDays, queueSettings.DryRunMode, failedFiles, quarantinedFiles));
        return new CleanupPlan(preview, quarantinedOriginals, failedOutputs);
    }

    private async Task<CleanupResult> PurgeExpiredQuarantinedOriginalsAsync(
        IReadOnlyList<Optimisarr.Data.Replacement> expired,
        int retentionDays,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (expired.Count == 0)
        {
            return CleanupResult.Empty;
        }

        var removed = 0;
        long reclaimedBytes = 0;
        var cutoff = now.AddDays(-retentionDays);
        foreach (var replacement in expired)
        {
            var path = replacement.QuarantinePath;
            await _db.Entry(replacement).ReloadAsync(cancellationToken);
            if (replacement.Status != ReplacementStatus.Replaced
                || replacement.ReplacedAt > cutoff
                || !string.Equals(replacement.QuarantinePath, path, PathComparison))
            {
                continue;
            }

            reclaimedBytes += DeleteQuarantinedOriginal(path);
            replacement.Status = ReplacementStatus.Purged;
            replacement.PurgedAt = now;
            removed++;
        }

        if (removed == 0)
        {
            return CleanupResult.Empty;
        }

        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation(
            "Purged {Count} quarantined original(s) past the {RetentionDays}-day cleanup window",
            removed, retentionDays);
        return new CleanupResult(removed, reclaimedBytes);
    }

    private async Task<CleanupResult> PurgeExpiredFailedOutputsAsync(
        IReadOnlyList<Job> expired,
        int retentionDays,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var cutoff = now.AddDays(-retentionDays);
        var removed = 0;
        long reclaimedBytes = 0;
        foreach (var job in expired)
        {
            var path = job.WorkOutputPath!;

            // Retry is an operator action and may race a slow sweep. Refresh this tracked row just
            // before touching disk, and also refuse a file written inside the retention window.
            // Together those checks protect both the database transition and a newly recreated path.
            await _db.Entry(job).ReloadAsync(cancellationToken);
            if (job.Status != JobStatus.Failed
                || !string.Equals(job.WorkOutputPath, path, PathComparison)
                || job.FinishedAt is null
                || job.FinishedAt > cutoff
                || WasWrittenAfter(path, cutoff.UtcDateTime)
                || await IsPathProtectedAsync(path, job.Id, cancellationToken))
            {
                continue;
            }

            var fileBytes = TryGetFileLength(path);
            if (!TryDeleteFailedOutput(path))
            {
                continue;
            }

            // Keep OutputSizeBytes and every diagnostic field. Only the disposable file reference
            // expires, so failure summaries remain useful after the cleanup window.
            job.WorkOutputPath = null;
            job.UpdatedAt = now;
            removed++;
            reclaimedBytes += fileBytes;
        }

        if (removed == 0)
        {
            return CleanupResult.Empty;
        }

        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation(
            "Removed {Count} failed work output(s) past the {RetentionDays}-day cleanup window; diagnostics retained",
            removed, retentionDays);
        return new CleanupResult(removed, reclaimedBytes);
    }

    /// <summary>
    /// Purges a single quarantined original on explicit operator approval, independent of the
    /// cleanup window. Dry-run mode still protects the original.
    /// </summary>
    public async Task<ReplacementActionResult> PurgeOneAsync(
        int replacementId,
        CancellationToken cancellationToken)
    {
        var queueSettings = await _settings.GetQueueSettingsAsync(cancellationToken);
        if (queueSettings.DryRunMode)
        {
            return ReplacementActionResult.Invalid(
                "Dry-run mode is enabled, so Optimisarr will keep quarantined originals and preserve rollback.");
        }

        var replacement = await _db.Replacements
            .FirstOrDefaultAsync(r => r.Id == replacementId, cancellationToken);

        if (replacement is null)
        {
            return ReplacementActionResult.NotFound($"No replacement with id {replacementId}.");
        }

        if (replacement.Status != ReplacementStatus.Replaced)
        {
            return ReplacementActionResult.Invalid(
                $"Replacement {replacementId} is {replacement.Status}; only a quarantined (Replaced) original can be purged.");
        }

        DeleteQuarantinedOriginal(replacement.QuarantinePath);
        replacement.Status = ReplacementStatus.Purged;
        replacement.PurgedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Purged quarantined original for replacement {ReplacementId} on operator approval", replacementId);
        return ReplacementActionResult.Ok(replacement);
    }

    private bool TryDeleteFailedOutput(string path)
    {
        if (!WorkPaths.IsUnderRoot(_workRoot, path))
        {
            _logger.LogWarning("Refused to clean failed output outside the work root: {Path}", path);
            return false;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            WorkPaths.PruneEmptyAncestors(_workRoot, path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Could not delete expired failed work output {Path}", path);
            return false;
        }
    }

    private Task<bool> IsPathProtectedAsync(
        string path,
        int currentJobId,
        CancellationToken cancellationToken) =>
        _db.Jobs.AnyAsync(job => job.Id != currentJobId
            && job.WorkOutputPath == path
            && (job.Status == JobStatus.Queued
                || job.Status == JobStatus.Probing
                || job.Status == JobStatus.Transcoding
                || job.Status == JobStatus.Verifying
                || job.Status == JobStatus.ReadyToReplace),
            cancellationToken);

    private long TryGetFileLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // An unreadable file is still eligible, but claiming its size would make the preview
            // misleading. Cleanup will retry the deletion independently and log any failure.
            _logger.LogWarning(exception, "Could not inspect retained file size {Path}", path);
            return 0;
        }
    }

    private static string BuildPlanToken(
        int retentionDays,
        bool dryRunMode,
        IReadOnlyList<CleanupFile> failedFiles,
        IReadOnlyList<CleanupFile> quarantinedFiles)
    {
        var identities = failedFiles
            .Concat(quarantinedFiles)
            .OrderBy(file => file.Kind, StringComparer.Ordinal)
            .ThenBy(file => file.Id)
            .Select(file => $"{file.Kind}\0{file.Id}\0{file.Path}\0{file.Bytes}");
        var payload = $"{retentionDays}\0{dryRunMode}\0{string.Join('\n', identities)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    private bool WasWrittenAfter(string path, DateTime cutoffUtc)
    {
        try
        {
            return File.Exists(path) && File.GetLastWriteTimeUtc(path) > cutoffUtc;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Treat unreadable recency as protected. A later sweep can retry after permissions are
            // corrected, while this pass continues cleaning unrelated outputs.
            _logger.LogWarning(exception, "Could not inspect failed work output age {Path}", path);
            return true;
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private static StringComparer PathComparer =>
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    // Best effort: preserve the established quarantine behaviour. The retention decision is
    // recorded even when a file was already absent or the filesystem refuses the delete.
    private long DeleteQuarantinedOriginal(string quarantinePath)
    {
        var fileBytes = TryGetFileLength(quarantinePath);
        try
        {
            if (File.Exists(quarantinePath))
            {
                File.Delete(quarantinePath);
            }

            var directory = Path.GetDirectoryName(quarantinePath);
            if (!string.IsNullOrEmpty(directory)
                && Directory.Exists(directory)
                && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }

            return File.Exists(quarantinePath) ? 0 : fileBytes;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete quarantined original {Path}", quarantinePath);
            return 0;
        }
    }

    private sealed record CleanupPlan(
        TimedCleanupPreview Preview,
        IReadOnlyList<Optimisarr.Data.Replacement> QuarantinedOriginals,
        IReadOnlyList<Job> FailedOutputs)
    {
        public static CleanupPlan Empty(int retentionDays, bool dryRunMode) => new(
            new TimedCleanupPreview(
                retentionDays,
                dryRunMode,
                0,
                0,
                0,
                0,
                BuildPlanToken(retentionDays, dryRunMode, [], [])),
            [],
            []);
    }

    private sealed record CleanupFile(string Kind, int Id, string Path, long Bytes);

    private sealed record CleanupResult(int Count, long Bytes)
    {
        public static CleanupResult Empty { get; } = new(0, 0);
    }
}
