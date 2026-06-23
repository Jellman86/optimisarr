using Microsoft.EntityFrameworkCore;
using Optimisarr.Data;

namespace Optimisarr.Api.Stats;

/// <summary>Aggregate library/queue/outcome figures for the Dashboard.</summary>
public sealed record StatsDto(
    long BytesSaved,
    long OriginalBytes,
    long OptimisedBytes,
    int FilesOptimised,
    double AverageSavingPercent,
    int InQuarantine,
    long QuarantineReclaimableBytes,
    int Queued,
    int Running,
    int ReadyToReplace,
    int Failed,
    int Libraries,
    int EnabledLibraries,
    long DiscoveredFiles);

public static class StatsQueries
{
    public static async Task<StatsDto> GetAsync(
        OptimisarrDbContext db, LifetimeStats lifetime, CancellationToken cancellationToken)
    {
        // Headline savings come from the durable lifetime tally, not from current rows, so the
        // figure survives quarantine purge, history clearing, and restarts. See LifetimeStatsStore.

        // Still-reversible entries; their originals are the space approving would reclaim.
        var quarantined = db.Replacements.AsNoTracking().Where(r => r.Status == ReplacementStatus.Replaced);
        var inQuarantine = await quarantined.CountAsync(cancellationToken);
        var quarantineReclaimableBytes = await quarantined.SumAsync(r => r.OriginalSizeBytes, cancellationToken);

        var queued = await db.Jobs.CountAsync(j => j.Status == JobStatus.Queued, cancellationToken);
        var running = await db.Jobs.CountAsync(
            j => j.Status == JobStatus.Probing || j.Status == JobStatus.Transcoding || j.Status == JobStatus.Verifying,
            cancellationToken);
        var readyToReplace = await db.Jobs.CountAsync(j => j.Status == JobStatus.ReadyToReplace, cancellationToken);
        var failed = await db.Jobs.CountAsync(j => j.Status == JobStatus.Failed, cancellationToken);

        var libraries = await db.Libraries.CountAsync(cancellationToken);
        var enabledLibraries = await db.Libraries.CountAsync(l => l.Enabled, cancellationToken);
        var discoveredFiles = await db.MediaFiles.LongCountAsync(cancellationToken);

        return new StatsDto(
            BytesSaved: lifetime.BytesSaved,
            OriginalBytes: lifetime.OriginalBytes,
            OptimisedBytes: lifetime.OptimisedBytes,
            FilesOptimised: (int)Math.Clamp(lifetime.FilesOptimised, 0, int.MaxValue),
            AverageSavingPercent: lifetime.AverageSavingPercent,
            InQuarantine: inQuarantine,
            QuarantineReclaimableBytes: quarantineReclaimableBytes,
            Queued: queued,
            Running: running,
            ReadyToReplace: readyToReplace,
            Failed: failed,
            Libraries: libraries,
            EnabledLibraries: enabledLibraries,
            DiscoveredFiles: discoveredFiles);
    }

    /// <summary>Size-weighted saving across all optimised files, as a percentage. 0 when nothing is optimised.</summary>
    public static double SavingPercent(long originalBytes, long optimisedBytes) =>
        originalBytes > 0 ? (1.0 - (double)optimisedBytes / originalBytes) * 100.0 : 0.0;
}
