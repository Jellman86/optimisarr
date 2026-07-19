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
            var file = await db.MediaFiles.FirstOrDefaultAsync(f => f.Id == request.MediaFileId, cancellationToken);
            if (file is null)
            {
                return ApiErrors.NotFound("media.notFound", $"No media file with id {request.MediaFileId}.", new { id = request.MediaFileId });
            }

            var exclusion = await db.Exclusions.FirstOrDefaultAsync(e => e.Path == file.Path, cancellationToken);
            if (exclusion is null)
            {
                exclusion = new Exclusion
                {
                    Path = file.Path,
                    LibraryId = file.LibraryId,
                    RelativePath = file.RelativePath,
                    Reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason!.Trim(),
                    Source = ExclusionSource.Manual
                };
                db.Exclusions.Add(exclusion);
            }

            // Tidy the queue: drop the file's failed/cancelled attempts so excluding from the Queue makes
            // them disappear. A running job is left to finish and a completed one is kept (it may own a
            // replacement); the exclusion is what stops the file being offered again.
            var spentJobs = await db.Jobs
                .Where(job => job.Type == JobType.Normal
                    && job.MediaFileId == file.Id
                    && (job.Status == JobStatus.Failed || job.Status == JobStatus.Cancelled))
                .ToListAsync(cancellationToken);
            db.Jobs.RemoveRange(spentJobs);

            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(new ExclusionDto(
                exclusion.Id, exclusion.Path, exclusion.LibraryId, exclusion.RelativePath,
                exclusion.Reason, exclusion.Source.ToString(), exclusion.CreatedAt));
        })
        .WithName("CreateExclusion");

        // Remove an exclusion — the file becomes eligible again under its library's rules.
        app.MapDelete("/api/exclusions/{id:int}", async (
            int id, OptimisarrDbContext db, CancellationToken cancellationToken) =>
        {
            var exclusion = await db.Exclusions.FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
            if (exclusion is null)
            {
                return ApiErrors.NotFound("exclusion.notFound", $"No exclusion with id {id}.", new { id });
            }

            db.Exclusions.Remove(exclusion);

            // Reset the file's failure streak so removing an exclusion gives it a genuine fresh start —
            // otherwise an auto-excluded file would be re-excluded on its very next failure.
            var media = await db.MediaFiles.FirstOrDefaultAsync(f => f.Path == exclusion.Path, cancellationToken);
            if (media is not null && media.FailureCount != 0)
            {
                media.FailureCount = 0;
                media.UpdatedAt = DateTimeOffset.UtcNow;
            }

            await db.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        })
        .WithName("DeleteExclusion");
    }
}
