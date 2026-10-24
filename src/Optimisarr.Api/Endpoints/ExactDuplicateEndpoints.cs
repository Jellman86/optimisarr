using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Optimisarr.Api.Library;
using Optimisarr.Data;

namespace Optimisarr.Api.Endpoints;

internal static class ExactDuplicateEndpoints
{
    public sealed record ReviewRequest(string ReviewKey, string Choice, int[] KeepIds);
    public static void MapExactDuplicateEndpoints(this WebApplication app)
    {
        app.MapGet("/api/libraries/{id:int}/duplicates", async (int id, int? page, OptimisarrDbContext db,
            ExactDuplicateCoordinator scans, CancellationToken token) =>
            await db.Libraries.AsNoTracking().AnyAsync(l => l.Id == id, token)
                ? Results.Ok(await scans.ReadAsync(id, Math.Clamp(page ?? 0, 0, 2500), token)) : ApiErrors.NotFound("library.notFound", "Library not found.", new { id }))
            .Produces<ExactDuplicateStatus>().Produces(StatusCodes.Status404NotFound)
            .WithName("GetExactDuplicates");

        app.MapPost("/api/libraries/{id:int}/duplicates", async (int id, bool? forceRefresh, [FromQuery] int[]? libraryIds, OptimisarrDbContext db,
            ExactDuplicateCoordinator scans, CancellationToken token) =>
        {
            var library = await db.Libraries.AsNoTracking().SingleOrDefaultAsync(l => l.Id == id, token);
            if (library is null) return ApiErrors.NotFound("library.notFound", "Library not found.", new { id });
            var ids = libraryIds is null || libraryIds.Length == 0 ? new[] { id } : libraryIds;
            if (ids.Length > 16 || !ids.Contains(id) || ids.Any(i => i < 1) || ids.Distinct().Count() != ids.Length)
                return ApiErrors.BadRequest("duplicates.scope", "Choose one to sixteen libraries, including the review library.");
            var selected = await db.Libraries.AsNoTracking().Where(l => ids.Contains(l.Id)).OrderBy(l => l.Id)
                .Select(l => new ExactDuplicateLibrary(l.Id, l.Name, l.Path, l.MediaType.ToString())).ToListAsync(token);
            if (selected.Count != ids.Length) return ApiErrors.BadRequest("duplicates.scope", "A selected library no longer exists. Refresh the library list.");
            // Only existing inventory entries are read. Work paths, trash and unindexed files
            // cannot become scan inputs. Skip files with unfinished jobs, including ready replacements.
            var files = await ExactDuplicateInputs.ReadAsync(db, ids, token);
            if (files.Count > ExactDuplicateInputs.MaximumCandidates) return ApiErrors.Conflict("duplicates.limit",
                "These libraries have too many candidates for this preview. Choose a smaller scope.");
            if (!await scans.TryStartAsync(id, library.Path, files, forceRefresh ?? false, token, selected)) return ApiErrors.Conflict("duplicates.busy",
                "A duplicate scan is already running. Finish or cancel it before starting another.");
            return Results.Accepted($"/api/libraries/{id}/duplicates", await scans.ReadAsync(id, 0, token));
        }).Produces<ExactDuplicateStatus>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status409Conflict).Produces(StatusCodes.Status404NotFound).Produces(StatusCodes.Status400BadRequest)
            .WithName("ScanExactDuplicates");

        app.MapDelete("/api/libraries/{id:int}/duplicates", (int id, ExactDuplicateCoordinator scans) =>
            scans.Cancel(id) ? Results.Accepted() : ApiErrors.Conflict("duplicates.notRunning", "This scan has already stopped."))
            .Produces(StatusCodes.Status202Accepted).Produces(StatusCodes.Status409Conflict)
            .WithName("CancelExactDuplicateScan");

        app.MapPut("/api/libraries/{id:int}/duplicates/review", async (int id, ReviewRequest request,
            ExactDuplicateStore store, CancellationToken token) =>
        {
            if (request.ReviewKey is null || request.Choice is null || request.KeepIds is null)
                return ApiErrors.BadRequest("duplicates.review", "Choose a review preference for this group.");
            return await store.DecideAsync(id, request.ReviewKey, request.Choice, request.KeepIds, token)
                ? Results.NoContent() : ApiErrors.Conflict("duplicates.reviewChanged", "This group or its scope changed. Scan again before saving a preference.");
        }).Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status409Conflict).Produces(StatusCodes.Status400BadRequest)
            .WithName("SaveExactDuplicateReview");
    }
}
