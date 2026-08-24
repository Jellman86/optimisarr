using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Library;
using Optimisarr.Api.Workers;
using Optimisarr.Core.Workers;
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
            if (job is null || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return ApiErrors.NotFound("worker.source.missing",
                    "The source for that lease is no longer on disk.");
            }

            // Hashed once and remembered. Repeating it per request would re-read gigabytes every
            // time a transfer resumed, and the value is what later proves a returned candidate was
            // encoded from these exact bytes rather than some other version of the file.
            if (string.IsNullOrWhiteSpace(job.SourceSha256))
            {
                job.SourceSha256 = await ComputeSha256Async(path, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
            }

            // Let the worker verify what it received without a second pass over the network, and
            // let it compare against what it will later be held to.
            response.Headers["X-Optimisarr-Source-Sha256"] = job.SourceSha256;
            response.Headers["X-Optimisarr-Job-Id"] = job.Id.ToString();

            var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                // Shared so a scan or probe reading the same original is not blocked by a transfer
                // that may take minutes.
                FileShare.Read,
                bufferSize: 128 * 1024,
                useAsync: true);

            // enableRangeProcessing is what makes a large transfer resumable: a worker whose
            // connection drops resumes with a Range header instead of pulling the whole file again.
            return Results.File(
                stream,
                contentType: "application/octet-stream",
                fileDownloadName: Path.GetFileName(path),
                enableRangeProcessing: true);
        })
        .WithName("FetchLeaseSource")
        .Produces<ApiError>(StatusCodes.Status401Unauthorized);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1024 * 1024, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
