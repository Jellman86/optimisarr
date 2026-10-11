using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Library;
using Optimisarr.Data;

namespace Optimisarr.Api.Endpoints;

internal static class ExactDuplicateEndpoints
{
    public static void MapExactDuplicateEndpoints(this WebApplication app)
    {
        app.MapGet("/api/libraries/{id:int}/duplicates", async (int id, int? page, OptimisarrDbContext db,
            ExactDuplicateCoordinator scans, CancellationToken token) =>
            await db.Libraries.AsNoTracking().AnyAsync(l => l.Id == id, token)
                ? Results.Ok(await scans.ReadAsync(id, Math.Clamp(page ?? 0, 0, 2500), token)) : ApiErrors.NotFound("library.notFound", "Library not found.", new { id }))
            .Produces<ExactDuplicateStatus>().Produces(StatusCodes.Status404NotFound)
            .WithName("GetExactDuplicates");

        app.MapPost("/api/libraries/{id:int}/duplicates", async (int id, bool? forceRefresh, OptimisarrDbContext db,
            ExactDuplicateCoordinator scans, CancellationToken token) =>
        {
            var library = await db.Libraries.AsNoTracking().SingleOrDefaultAsync(l => l.Id == id, token);
            if (library is null) return ApiErrors.NotFound("library.notFound", "Library not found.", new { id });
            // Only existing inventory entries are read. Work paths, trash and unindexed files
            // cannot become scan inputs. Skip files with unfinished jobs, including ready replacements.
            var files = await ExactDuplicateInputs.ReadAsync(db, id, token);
            if (files.Count > ExactDuplicateInputs.MaximumCandidates) return ApiErrors.Conflict("duplicates.limit",
                "This library has too many candidates for this preview. Use a smaller library.");
            if (!await scans.TryStartAsync(id, library.Path, files, forceRefresh ?? false, token)) return ApiErrors.Conflict("duplicates.busy",
                "A duplicate scan is already running. Finish or cancel it before starting another.");
            return Results.Accepted($"/api/libraries/{id}/duplicates", await scans.ReadAsync(id, 0, token));
        }).Produces<ExactDuplicateStatus>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status409Conflict).Produces(StatusCodes.Status404NotFound)
            .WithName("ScanExactDuplicates");

        app.MapDelete("/api/libraries/{id:int}/duplicates", (int id, ExactDuplicateCoordinator scans) =>
            scans.Cancel(id) ? Results.Accepted() : ApiErrors.Conflict("duplicates.notRunning", "This scan has already stopped."))
            .Produces(StatusCodes.Status202Accepted).Produces(StatusCodes.Status409Conflict)
            .WithName("CancelExactDuplicateScan");
    }
}
