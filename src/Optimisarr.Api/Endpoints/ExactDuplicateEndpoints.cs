using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Library;
using Optimisarr.Data;

namespace Optimisarr.Api.Endpoints;

internal static class ExactDuplicateEndpoints
{
    public static void MapExactDuplicateEndpoints(this WebApplication app)
    {
        app.MapGet("/api/libraries/{id:int}/duplicates", async (int id, OptimisarrDbContext db,
            ExactDuplicateCoordinator scans, CancellationToken token) =>
            await db.Libraries.AnyAsync(l => l.Id == id, token)
                ? Results.Ok(scans.Read(id)) : ApiErrors.NotFound("library.notFound", "Library not found.", new { id }))
            .Produces<ExactDuplicateStatus>().Produces(StatusCodes.Status404NotFound)
            .WithName("GetExactDuplicates");

        app.MapPost("/api/libraries/{id:int}/duplicates", async (int id, OptimisarrDbContext db,
            ExactDuplicateCoordinator scans, CancellationToken token) =>
        {
            var library = await db.Libraries.AsNoTracking().SingleOrDefaultAsync(l => l.Id == id, token);
            if (library is null) return ApiErrors.NotFound("library.notFound", "Library not found.", new { id });
            // Only existing inventory entries are read. Work paths, trash and unindexed files
            // cannot become scan inputs. Skip files with unfinished jobs, including ready replacements.
            var sizes = db.MediaFiles.Where(f => f.LibraryId == id && f.SizeBytes > 0)
                .GroupBy(f => f.SizeBytes).Where(g => g.Count() > 1).Select(g => g.Key);
            var files = await db.MediaFiles.AsNoTracking().Where(f => f.LibraryId == id && sizes.Contains(f.SizeBytes)
                && !db.Jobs.Any(j => j.MediaFileId == f.Id && j.Status != JobStatus.Completed
                    && j.Status != JobStatus.Cancelled && j.Status != JobStatus.Failed))
                .OrderBy(f => f.Id).Select(f => new ExactDuplicateInput(f.Id, f.Path, f.RelativePath, f.SizeBytes, f.ModifiedAt))
                .Take(50_001).ToListAsync(token);
            if (files.Count > 50_000) return ApiErrors.Conflict("duplicates.limit",
                "This library has too many candidates for this preview. Use a smaller library.");
            if (!scans.TryStart(id, library.Path, files)) return ApiErrors.Conflict("duplicates.busy",
                "A duplicate scan is already running. Finish or cancel it before starting another.");
            return Results.Accepted($"/api/libraries/{id}/duplicates", scans.Read(id));
        }).Produces<ExactDuplicateStatus>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status409Conflict).Produces(StatusCodes.Status404NotFound)
            .WithName("ScanExactDuplicates");

        app.MapDelete("/api/libraries/{id:int}/duplicates", (int id, ExactDuplicateCoordinator scans) =>
            scans.Cancel(id) ? Results.Accepted() : ApiErrors.Conflict("duplicates.notRunning", "This scan has already stopped."))
            .Produces(StatusCodes.Status202Accepted).Produces(StatusCodes.Status409Conflict)
            .WithName("CancelExactDuplicateScan");
    }
}
