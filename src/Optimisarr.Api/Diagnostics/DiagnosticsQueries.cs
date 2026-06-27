using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Library;
using Optimisarr.Api.Queue;
using Optimisarr.Api.Stats;
using Optimisarr.Data;

namespace Optimisarr.Api.Diagnostics;

internal static class DiagnosticsQueries
{
    /// <summary>
    /// Assembles the admin diagnostics bundle from read-only, non-secret sources: settings, lifetime
    /// stats, per-library summaries, redacted integration summaries, and the failure summary. The
    /// caller supplies the environment facts (it owns the resolved config path).
    /// </summary>
    public static async Task<DiagnosticsBundle> BuildAsync(
        OptimisarrDbContext db,
        SettingsStore settings,
        LifetimeStatsStore lifetimeStats,
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

        return new DiagnosticsBundle(
            "optimisarr",
            version,
            DateTimeOffset.UtcNow,
            environment,
            SettingsDto.From(queue),
            stats,
            libraries,
            integrations,
            failures);
    }
}
