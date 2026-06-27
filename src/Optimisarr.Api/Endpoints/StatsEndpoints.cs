using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Diagnostics;
using Optimisarr.Api.Endpoints;
using Optimisarr.Api.Library;
using Optimisarr.Api.Metrics;
using Optimisarr.Api.Queue;
using Optimisarr.Api.Realtime;
using Optimisarr.Api.Replacement;
using Optimisarr.Api.Security;
using Optimisarr.Api.Stats;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Library;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Rules;
using Optimisarr.Core.Settings;
using Optimisarr.Core.Tools;
using Optimisarr.Core.Verification;
using Optimisarr.Data;

namespace Optimisarr.Api.Endpoints;

internal static class StatsEndpoints
{
    public static void MapStatsEndpoints(this WebApplication app, string configDirectory)
    {
        // Aggregate outcome/queue/library figures for the Dashboard.
        app.MapGet("/api/stats", async (
            OptimisarrDbContext db, LifetimeStatsStore lifetimeStats, CancellationToken cancellationToken) =>
        {
            var lifetime = await lifetimeStats.GetAsync(cancellationToken);
            return Results.Ok(await StatsQueries.GetAsync(db, lifetime, cancellationToken));
        })
        .WithName("GetStats");

        // Admin-only support snapshot: version, environment, settings, library and integration summaries,
        // stats, and the failure summary — assembled from non-secret data only (no provider tokens, API keys,
        // or webhook URLs). It is under /api, so the admin-token middleware protects it when a token is set.
        app.MapGet("/api/diagnostics", async (
            OptimisarrDbContext db,
            SettingsStore settings,
            LifetimeStatsStore lifetimeStats,
            CancellationToken cancellationToken) =>
        {
            var environment = new DiagnosticsEnvironment(
                RuntimeInformation.OSDescription,
                RuntimeInformation.FrameworkDescription,
                configDirectory,
                PathAccessProbe.CanWrite(configDirectory));
            var version = typeof(Program).Assembly.GetName().Version?.ToString();

            return Results.Ok(await DiagnosticsQueries.BuildAsync(
                db, settings, lifetimeStats, environment, version, cancellationToken));
        })
        .WithName("GetDiagnostics");

        // Reset the persistent lifetime "total space saved" tally to zero. This only clears the headline
        // figure the operator chose to reset; it touches no files, quarantine, or rollback history.
        app.MapPost("/api/stats/clear", async (
            OptimisarrDbContext db, LifetimeStatsStore lifetimeStats, CancellationToken cancellationToken) =>
        {
            await lifetimeStats.ClearAsync(cancellationToken);
            return Results.Ok(await StatsQueries.GetAsync(db, LifetimeStats.Empty, cancellationToken));
        })
        .WithName("ClearStats");
    }
}
