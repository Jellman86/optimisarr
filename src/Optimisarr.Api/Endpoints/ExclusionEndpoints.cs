using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Diagnostics;
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

internal static class ExclusionEndpoints
{
    public static void MapExclusionEndpoints(this WebApplication app)
    {
        app.MapGet("/api/exclusions", async (
            int? libraryId, OptimisarrDbContext db, CancellationToken cancellationToken) =>
        {
            var query = db.Exclusions.AsNoTracking();
            if (libraryId is not null)
            {
                query = query.Where(exclusion => exclusion.LibraryId == libraryId);
            }

            // Materialise first, then order and map on the client: SQLite can't ORDER BY a DateTimeOffset,
            // and the enum-to-string conversion isn't translatable in a projection either.
            var rows = await query.ToListAsync(cancellationToken);

            var items = rows
                .OrderByDescending(exclusion => exclusion.CreatedAt)
                .Select(exclusion => new ExclusionDto(
                    exclusion.Id, exclusion.Path, exclusion.LibraryId, exclusion.RelativePath,
                    exclusion.Reason, exclusion.Source.ToString(), exclusion.CreatedAt));

            return Results.Ok(items);
        })
        .WithName("ListExclusions");

        // Exclude a file so it is never offered for optimisation again. Identified by media-file id so
        // the caller (Queue or Candidates) need not know the absolute path. Idempotent on the file's path.
        app.MapPost("/api/exclusions", async (
            ExcludeRequest request, OptimisarrDbContext db, CancellationToken cancellationToken) =>
        {
            // The rule lives in ExclusionOperations so the batch endpoint applies exactly the same
            // checks; this endpoint only translates the outcome into an HTTP status.
            var outcome = await ExclusionOperations.ApplyAsync(db, request.MediaFileId, request.Reason, cancellationToken);
            if (!outcome.Succeeded)
            {
                return ApiErrors.NotFound(outcome.Code, outcome.Message, new { id = request.MediaFileId });
            }

            var exclusion = outcome.Exclusion!;
            return Results.Ok(new ExclusionDto(
                exclusion.Id, exclusion.Path, exclusion.LibraryId, exclusion.RelativePath,
                exclusion.Reason, exclusion.Source.ToString(), exclusion.CreatedAt));
        })
        .WithName("CreateExclusion");

        // Remove an exclusion — the file becomes eligible again under its library's rules.
        app.MapDelete("/api/exclusions/{id:int}", async (
            int id, OptimisarrDbContext db, CancellationToken cancellationToken) =>
        {
            // The rule lives in ExclusionOperations so the batch endpoint applies exactly the same
            // checks; this endpoint only translates the outcome into an HTTP status.
            var outcome = await ExclusionOperations.UndoAsync(db, id, cancellationToken);
            if (!outcome.Succeeded)
            {
                return ApiErrors.NotFound(outcome.Code, outcome.Message, new { id });
            }

            return Results.NoContent();
        })
        .WithName("DeleteExclusion");
    }
}
