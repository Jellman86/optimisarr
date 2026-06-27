using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Api;
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

internal static class ReplacementEndpoints
{
    public static void MapReplacementEndpoints(this WebApplication app)
    {
        app.MapGet("/api/replacements", async (OptimisarrDbContext db, CancellationToken cancellationToken) =>
            Results.Ok(await ReplacementQueries.ListAsync(db, cancellationToken)))
        .WithName("ListReplacements");

        // Remove spent quarantine entries (rolled back, or purged after retention/approval) to declutter.
        // These are terminal history with no rollback left, so clearing them never touches a live original;
        // active "Replaced" entries are always kept.
        app.MapPost("/api/replacements/clear", async (OptimisarrDbContext db, CancellationToken cancellationToken) =>
        {
            var spent = await db.Replacements
                .Where(r => r.Status == ReplacementStatus.RolledBack || r.Status == ReplacementStatus.Purged)
                .ToListAsync(cancellationToken);
            db.Replacements.RemoveRange(spent);
            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(new { cleared = spent.Count });
        })
        .WithName("ClearSpentReplacements");

        // One replacement with its job's verification report, for the Quarantine compare-to-approve view.
        app.MapGet("/api/replacements/{id:int}", async (
            int id,
            OptimisarrDbContext db,
            CancellationToken cancellationToken) =>
        {
            var detail = await ReplacementQueries.GetAsync(db, id, cancellationToken);
            return detail is null ? Results.NotFound(new { error = $"No replacement with id {id}." }) : Results.Ok(detail);
        })
        .WithName("GetReplacement");

        // Streams the two sides of a replacement for the Quarantine visual compare: the quarantined
        // original (under /trash) and the in-place replacement (the encoded file now at the media path).
        app.MapGet("/api/replacements/{id:int}/original/content", async (
            int id,
            OptimisarrDbContext db,
            CancellationToken cancellationToken) =>
        {
            var path = await db.Replacements
                .Where(r => r.Id == id)
                .Select(r => r.QuarantinePath)
                .FirstOrDefaultAsync(cancellationToken);
            return FileServing.ServeFile(path);
        })
        .WithName("GetReplacementOriginalContent");

        app.MapGet("/api/replacements/{id:int}/replacement/content", async (
            int id,
            OptimisarrDbContext db,
            CancellationToken cancellationToken) =>
        {
            var path = await db.Replacements
                .Where(r => r.Id == id)
                .Select(r => r.FinalPath)
                .FirstOrDefaultAsync(cancellationToken);
            return FileServing.ServeFile(path);
        })
        .WithName("GetReplacementReplacementContent");

        app.MapPost("/api/replacements/{id:int}/rollback", async (
            int id,
            ReplacementService replacement,
            CancellationToken cancellationToken) =>
        {
            var result = await replacement.RollbackAsync(id, cancellationToken);
            return ReplacementResults.ToHttp(result);
        })
        .WithName("RollbackReplacement");

        // Approve a replacement: delete the quarantined original now (reclaim space) instead of waiting
        // for the retention window. The replacement is kept; this only discards the rollback path.
        app.MapPost("/api/replacements/{id:int}/approve", async (
            int id,
            QuarantinePurgeService purge,
            CancellationToken cancellationToken) =>
        {
            var result = await purge.PurgeOneAsync(id, cancellationToken);
            return ReplacementResults.ToHttp(result);
        })
        .WithName("ApproveReplacement");
    }
}
