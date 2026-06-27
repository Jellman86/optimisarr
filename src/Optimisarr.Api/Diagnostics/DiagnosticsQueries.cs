using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Library;
using Optimisarr.Api.Queue;
using Optimisarr.Api.Stats;
using Optimisarr.Core.Tools;
using Optimisarr.Data;

namespace Optimisarr.Api.Diagnostics;

internal static class DiagnosticsQueries
{
    private const int RecentLogCount = 3;

    /// <summary>
    /// Assembles the admin diagnostics bundle from read-only, non-secret sources: settings, lifetime
    /// stats, per-library summaries, redacted integration summaries, and the failure summary. The
    /// caller supplies the environment facts (it owns the resolved config path).
    /// </summary>
    public static async Task<DiagnosticsBundle> BuildAsync(
        OptimisarrDbContext db,
        SettingsStore settings,
        LifetimeStatsStore lifetimeStats,
        IReadOnlyList<ToolCheckResult> toolChecks,
        HardwareCapabilityResult hardwareCapability,
        DiagnosticsEnvironment environment,
        string? version,
        CancellationToken cancellationToken)
    {
        var queue = await settings.GetQueueSettingsAsync(cancellationToken);
        var lifetime = await lifetimeStats.GetAsync(cancellationToken);
        var stats = await StatsQueries.GetAsync(db, lifetime, cancellationToken);

        var fileCounts = (await db.MediaFiles
                .AsNoTracking()
                .Where(file => file.LibraryId != null)
                .GroupBy(file => file.LibraryId!.Value)
                .Select(group => new { LibraryId = group.Key, Count = group.Count() })
                .ToListAsync(cancellationToken))
            .ToDictionary(row => row.LibraryId, row => row.Count);

        var libraries = (await db.Libraries
                .AsNoTracking()
                .OrderBy(library => library.Name)
                .Select(library => new { library.Id, library.Name, library.MediaType, library.RuleProfile, library.Enabled })
                .ToListAsync(cancellationToken))
            .Select(library => new DiagnosticsLibrary(
                library.Id,
                library.Name,
                library.MediaType.ToString(),
                library.RuleProfile.ToString(),
                library.Enabled,
                fileCounts.GetValueOrDefault(library.Id)))
            .ToList();

        var watchers = await db.ActivityWatchers.AsNoTracking().OrderBy(w => w.Name).ToListAsync(cancellationToken);
        var targets = await db.NotificationTargets.AsNoTracking().OrderBy(t => t.Name).ToListAsync(cancellationToken);
        var connections = await db.ArrConnections.AsNoTracking().OrderBy(c => c.Name).ToListAsync(cancellationToken);
        var integrations = DiagnosticsRedaction.Summarise(watchers, targets, connections);

        var failures = await JobQueries.SummariseFailuresAsync(db, cancellationToken);

        // A few most-recent captured ffmpeg logs. The log is ffmpeg's own stderr (no provider
        // secrets); ordering is in memory because SQLite cannot ORDER BY the DateTimeOffset column.
        var recentLogs = (await db.Jobs
                .AsNoTracking()
                .Where(job => job.Status == JobStatus.Failed && job.ProcessLog != null)
                .Select(job => new
                {
                    job.Id,
                    RelativePath = job.MediaFile != null ? job.MediaFile.RelativePath : null,
                    job.ProcessLog,
                    job.FinishedAt
                })
                .ToListAsync(cancellationToken))
            .OrderByDescending(job => job.FinishedAt)
            .Take(RecentLogCount)
            .Select(job => new DiagnosticsLog(job.Id, job.RelativePath, job.ProcessLog!))
            .ToList();

        return new DiagnosticsBundle(
            "optimisarr",
            version,
            DateTimeOffset.UtcNow,
            environment,
            SettingsDto.From(queue),
            stats,
            toolChecks,
            hardwareCapability,
            libraries,
            integrations,
            failures,
            recentLogs);
    }
}
