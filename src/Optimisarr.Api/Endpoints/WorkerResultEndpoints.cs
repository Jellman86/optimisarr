using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Library;
using Optimisarr.Api.Queue;
using Optimisarr.Api.Workers;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Workers;
using Optimisarr.Data;

namespace Optimisarr.Api.Endpoints;

internal sealed record ResultAcceptedDto(int JobId, long Bytes, string CandidateSha256);

internal static class WorkerResultEndpoints
{
    private const string SourceHashHeader = "X-Optimisarr-Source-Sha256";
    private const string CandidateHashHeader = "X-Optimisarr-Candidate-Sha256";

    public static void MapWorkerResultEndpoints(this WebApplication app)
    {
        // Takes delivery of a candidate encoded elsewhere.
        //
        // This is the point where bytes from another machine enter the pipeline, so the checks run
        // in an order chosen for what each one protects: authenticate first, so nothing about a
        // lease is revealed to a caller with no claim on it; then prove the claim is live; then
        // prove the candidate is about *this* source; and only then write anything to disk.
        //
        // The candidate goes to the work directory a local transcode would have used. It does not
        // go near the original, and nothing here marks the job replaceable — verification has not
        // run yet, and a candidate that has not been verified must never be a replacement.
        app.MapPost("/api/workers/leases/{leaseId:guid}/result", async (
            Guid leaseId,
            HttpRequest http,
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

            // The late-result and duplicate-delivery cases together. A lease that has lapsed may
            // have had its job reassigned, and one already completed has had its result; in neither
            // case is there a live claim for this candidate to arrive through.
            if (lease.ToDomain().StateAt(DateTimeOffset.UtcNow) != LeaseState.Held)
            {
                return ApiErrors.Conflict("worker.lease.notHeld",
                    "That lease is no longer held, so a result cannot be delivered through it.");
            }

            var job = lease.Job;
            if (job?.MediaFile is null)
            {
                return ApiErrors.NotFound("worker.source.missing", "That lease has no source.");
            }

            // The lease being live is not enough: an operator may have cancelled the job while the
            // worker was still encoding. A candidate for a job that is no longer leased has nowhere
            // to go, and accepting it would quietly revive work someone chose to stop.
            if (job.Status != JobStatus.Leased)
            {
                return ApiErrors.Conflict("worker.result.jobNotLeased",
                    $"That job is {job.Status}, not leased, so a result cannot be delivered for it.");
            }

            var claimedSource = http.Headers[SourceHashHeader].ToString();
            var claimedCandidate = http.Headers[CandidateHashHeader].ToString();

            if (string.IsNullOrWhiteSpace(claimedSource) || string.IsNullOrWhiteSpace(claimedCandidate))
            {
                // Fail closed on missing evidence rather than accepting an unattributable file.
                return ApiErrors.BadRequest("worker.result.evidenceMissing",
                    $"Both {SourceHashHeader} and {CandidateHashHeader} are required.");
            }

            // The check the source hash exists for. A candidate encoded from different bytes is not
            // evidence about this job whatever its quality, and quietly accepting one would mean
            // verifying — and potentially replacing — against the wrong original.
            if (!string.Equals(job.SourceSha256, claimedSource, StringComparison.OrdinalIgnoreCase))
            {
                return ApiErrors.Conflict("worker.result.sourceMismatch",
                    "That candidate was encoded from a different source than this job's.");
            }

            // The candidate takes the container extension the assignment promised, recorded on the
            // lease when it was granted. The replacement names the final file from the candidate,
            // so naming it after the source would place an MP4 under ".mkv". A lease with no
            // recorded contract cannot be delivered against.
            if (string.IsNullOrWhiteSpace(lease.OutputExtension))
            {
                return ApiErrors.Conflict("worker.result.contractMissing",
                    "That lease records no output container, so its candidate cannot be named safely.");
            }

            var workRoot = WorkPaths.Resolve(environment);
            var outputRoot = WorkOutputRoot.ForMediaFile(workRoot, job.MediaFileId);
            Directory.CreateDirectory(outputRoot);

            // Written under a temporary name and hashed on the way in, so a transfer that dies
            // part-way never leaves something that looks like a finished candidate.
            var finalPath = RemoteCandidate.PathFor(outputRoot, job.Id, "." + lease.OutputExtension.TrimStart('.'));
            var stagingPath = finalPath + ".partial";

            long written;
            string actualHash;
            try
            {
                (written, actualHash) = await StreamToFileAsync(http.Body, stagingPath, cancellationToken);
            }
            catch (Exception)
            {
                TryDelete(stagingPath);
                throw;
            }

            if (!string.Equals(actualHash, claimedCandidate, StringComparison.OrdinalIgnoreCase))
            {
                // Truncated, corrupted, or misdescribed. Any of those verified as a real candidate
                // could end up replacing an original with a broken file.
                TryDelete(stagingPath);
                return ApiErrors.Conflict("worker.result.hashMismatch",
                    "The uploaded candidate does not match the hash the worker declared.");
            }

            TryDelete(finalPath);
            File.Move(stagingPath, finalPath);

            job.WorkOutputPath = finalPath;

            // Deliberately not ReadyToReplace. Verification has not run, and a candidate produced
            // elsewhere earns nothing until every local gate has been repeated against it. Nor
            // Verifying: that means verification is running here now, and restart recovery would
            // rightly discard it as interrupted. The dispatcher picks this status up in its turn.
            job.Status = JobStatus.AwaitingVerification;

            lease.Apply(lease.ToDomain().Complete(worker.Id, DateTimeOffset.UtcNow).Lease);
            await db.SaveChangesAsync(cancellationToken);

            // Accepted rather than OK: the candidate is delivered and intact, not yet judged.
            return Results.Accepted(value: new ResultAcceptedDto(job.Id, written, actualHash));
        })
        .WithName("DeliverResult")
        // A candidate is a whole film. Kestrel's default 30 MB body cap exists for form posts, and
        // the first real delivery from a Mac hit it; the body streams to disk in bounded chunks
        // above, so lifting the cap here costs no memory. Found by the live work-loop test.
        .WithMetadata(new DisableRequestSizeLimitAttribute())
        .Produces<ResultAcceptedDto>(StatusCodes.Status202Accepted)
        .Produces<ApiError>(StatusCodes.Status401Unauthorized);
    }

    /// <summary>
    /// Streams the body to disk while hashing it, so a multi-gigabyte candidate is never held in
    /// memory and the hash costs no extra pass over the data.
    /// </summary>
    private static async Task<(long Bytes, string Sha256)> StreamToFileAsync(
        Stream body,
        string path,
        CancellationToken cancellationToken)
    {
        using var sha = SHA256.Create();
        var buffer = new byte[128 * 1024];
        long total = 0;

        await using (var file = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.None, buffer.Length, useAsync: true))
        {
            int read;
            while ((read = await body.ReadAsync(buffer, cancellationToken)) > 0)
            {
                sha.TransformBlock(buffer, 0, read, null, 0);
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                total += read;
            }
            sha.TransformFinalBlock([], 0, 0);
        }

        return (total, Convert.ToHexString(sha.Hash!).ToLowerInvariant());
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A leftover staging file is untidy, not dangerous — it is never treated as a candidate.
        }
    }
}
