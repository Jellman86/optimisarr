using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Library;
using Optimisarr.Api.Queue;
using Optimisarr.Api.Replacement;
using Optimisarr.Api.Workers;
using Optimisarr.Core.Workers;
using Optimisarr.Core.Library;
using Optimisarr.Data;

namespace Optimisarr.Api.Endpoints;

internal static class WorkerSourceEndpoints
{
    public static void MapWorkerSourceEndpoints(this WebApplication app)
    {
        // Streams the source a worker has been assigned.
        //
        // The single most important property here is that the worker names nothing. It presents a
        // lease id; the server resolves lease -> job -> media file -> path. There is deliberately no
        // path, filename, or library parameter anywhere in this route, because any of them would
        // turn a paired sidecar into an arbitrary file reader on the host. A worker can only ever
        // fetch the exact file the control plane already decided to give it.
        //
        // Read-only throughout: the original is opened for shared reading and never written,
        // moved, or truncated. Optimisarr remains the only thing that touches originals.
        app.MapGet("/api/workers/leases/{leaseId:guid}/source", async (
            Guid leaseId,
            HttpRequest http,
            HttpResponse response,
            SettingsStore settings,
            OptimisarrDbContext db,
            IHostEnvironment environment,
            CancellationToken cancellationToken) =>
        {
            if (await WorkerGate.RefusedAsync(settings, cancellationToken) is { } refused)
            {
                return refused;
            }

            var worker = await WorkerAuth.ResolveAsync(http, db, cancellationToken);
            if (worker is null)
            {
                return WorkerGate.Unauthenticated();
            }

            var lease = await db.JobLeases
                .Include(l => l.Job)
                .ThenInclude(job => job!.MediaFile)
                .ThenInclude(file => file!.Library)
                .FirstOrDefaultAsync(l => l.Id == leaseId, cancellationToken);

            if (lease is null)
            {
                return ApiErrors.NotFound("worker.lease.notFound", $"No lease with id {leaseId}.");
            }

            if (lease.WorkerId != worker.Id)
            {
                return Results.Json(
                    new ApiError("worker.lease.notHolder", "That lease belongs to another worker."),
                    statusCode: StatusCodes.Status403Forbidden);
            }

            // A lapsed lease grants nothing. Otherwise a worker that lost its claim could keep
            // pulling media indefinitely, which is exactly the access the lease is meant to bound.
            if (lease.ToDomain().StateAt(DateTimeOffset.UtcNow) != LeaseState.Held)
            {
                return ApiErrors.Conflict("worker.lease.expired",
                    "That lease is no longer held, so its source is no longer available.");
            }

            var job = lease.Job;
            var path = job?.MediaFile?.Path;
            if (job is null || string.IsNullOrWhiteSpace(path)
                || !LibraryPathSafety.IsSafeFile(path, job.MediaFile?.Library?.Path))
            {
                return ApiErrors.NotFound("worker.source.missing",
                    "The source for that lease is no longer on disk.");
            }

            var readRoot = job.MediaFile?.Library?.Path;
            if (job.Type != JobType.Normal)
            {
                var sample = WorkerSampleSource.FromLease(job, lease, WorkPaths.Resolve(environment));
                if (sample is null)
                    return ApiErrors.Conflict("worker.source.sampleMissing", "This interactive assignment has no frozen source sample.");
                path = sample.Path;
                readRoot = WorkerJobPaths.Root(WorkPaths.Resolve(environment), job);
            }
            var stream = FileServing.OpenLibraryRead(path, readRoot);
            if (stream is null)
                return ApiErrors.NotFound("worker.source.missing", "The source path is no longer available safely.");
            try
            {
                // Hash and transfer the same open file. Remembering the hash avoids re-reading
                // gigabytes on each resumed request; it is not evidence that the path is still safe.
                if (string.IsNullOrWhiteSpace(job.SourceSha256))
                {
                    var hash = await SHA256.HashDataAsync(stream, cancellationToken);
                    job.SourceSha256 = Convert.ToHexString(hash).ToLowerInvariant();
                    stream.Position = 0;
                    await db.SaveChangesAsync(cancellationToken);
                }

                if (!LibraryPathSafety.IsSafeFile(path, readRoot))
                {
                    await stream.DisposeAsync();
                    return ApiErrors.NotFound("worker.source.missing", "The source path is no longer available safely.");
                }
                response.Headers["X-Optimisarr-Source-Sha256"] = job.SourceSha256;
                response.Headers["X-Optimisarr-Job-Id"] = job.Id.ToString();
                return Results.File(stream, contentType: "application/octet-stream",
                    fileDownloadName: Path.GetFileName(path), enableRangeProcessing: true);
            }
            catch
            {
                await stream.DisposeAsync();
                throw;
            }
        })
        .WithName("FetchLeaseSource")
        .Produces<ApiError>(StatusCodes.Status401Unauthorized);

        // The poster of the leased title, for the sidecar's own status page. The same lease rules
        // as the source: the worker names only its lease, and only while it holds it, so this
        // cannot be used to walk the library's artwork. 404 when nothing resolves.
        app.MapGet("/api/workers/leases/{leaseId:guid}/artwork", async (
            Guid leaseId,
            HttpRequest http,
            SettingsStore settings,
            OptimisarrDbContext db,
            IHostEnvironment environment,
            ArtworkService artwork,
            CancellationToken cancellationToken) =>
        {
            if (await WorkerGate.RefusedAsync(settings, cancellationToken) is { } refused)
            {
                return refused;
            }

            var worker = await WorkerAuth.ResolveAsync(http, db, cancellationToken);
            if (worker is null)
            {
                return WorkerGate.Unauthenticated();
            }

            var lease = await db.JobLeases.AsNoTracking().Include(l => l.Job)
                .FirstOrDefaultAsync(l => l.Id == leaseId, cancellationToken);
            if (lease is null)
            {
                return ApiErrors.NotFound("worker.lease.notFound", $"No lease with id {leaseId}.");
            }
            if (lease.WorkerId != worker.Id)
            {
                return Results.Json(
                    new ApiError("worker.lease.notHolder", "That lease belongs to another worker."),
                    statusCode: StatusCodes.Status403Forbidden);
            }
            if (lease.ToDomain().StateAt(DateTimeOffset.UtcNow) != LeaseState.Held)
            {
                return ApiErrors.Conflict("worker.lease.expired",
                    "That lease is no longer held, so its artwork is no longer available.");
            }

            return lease.Job is { } job && await artwork.GetThumbnailAsync(job.MediaFileId, cancellationToken) is { } poster
                ? Results.File(poster.Bytes, poster.ContentType)
                : Results.NotFound();
        })
        .WithName("FetchLeaseArtwork")
        .Produces<ApiError>(StatusCodes.Status401Unauthorized)
        .Produces<ApiError>(StatusCodes.Status403Forbidden)
        .Produces<ApiError>(StatusCodes.Status409Conflict);
    }
}
