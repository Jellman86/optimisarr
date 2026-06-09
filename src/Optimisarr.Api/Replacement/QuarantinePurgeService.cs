using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Library;
using Optimisarr.Core.Replacement;
using Optimisarr.Data;

namespace Optimisarr.Api.Replacement;

/// <summary>
/// Enforces the quarantine retention policy: once a replaced original has sat in
/// quarantine longer than the configured retention window, its file is deleted and
/// the replacement is marked <see cref="ReplacementStatus.Purged"/>. This is the one
/// place that intentionally discards the rollback path, so it is deliberately
/// conservative — it only ever touches <see cref="ReplacementStatus.Replaced"/> rows,
/// and a retention of zero (the default) purges nothing.
/// </summary>
public sealed class QuarantinePurgeService(
    OptimisarrDbContext db,
    SettingsStore settings,
    ILogger<QuarantinePurgeService> logger)
{
    /// <summary>Purges expired quarantined originals and returns how many were purged.</summary>
    public async Task<int> PurgeExpiredAsync(CancellationToken cancellationToken)
    {
        var queueSettings = await settings.GetQueueSettingsAsync(cancellationToken);
        var retentionDays = queueSettings.ReplacementQuarantineRetentionDays;
        if (retentionDays <= 0)
        {
            return 0;
        }

        var replaced = await db.Replacements
            .Where(replacement => replacement.Status == ReplacementStatus.Replaced)
            .ToListAsync(cancellationToken);

        var entries = replaced
            .Select(replacement => new QuarantineEntry(replacement.Id, replacement.ReplacedAt))
            .ToList();
        var expiredIds = QuarantineRetentionEvaluator
            .FindExpired(entries, retentionDays, DateTimeOffset.UtcNow)
            .ToHashSet();
        if (expiredIds.Count == 0)
        {
            return 0;
        }

        var purgedAt = DateTimeOffset.UtcNow;
        foreach (var replacement in replaced.Where(replacement => expiredIds.Contains(replacement.Id)))
        {
            DeleteQuarantinedOriginal(replacement.QuarantinePath);
            replacement.Status = ReplacementStatus.Purged;
            replacement.PurgedAt = purgedAt;
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Purged {Count} quarantined original(s) past the {RetentionDays}-day retention window",
            expiredIds.Count, retentionDays);
        return expiredIds.Count;
    }

    // Best effort: the retention decision is already committed in the status change,
    // so a file we cannot delete (already gone, permissions) must not abort the purge.
    // The original's per-timestamp quarantine folder holds only this file, so remove
    // it too once it is empty rather than leaving an orphan directory behind.
    private void DeleteQuarantinedOriginal(string quarantinePath)
    {
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
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not delete quarantined original {Path}", quarantinePath);
        }
    }
}
