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

/// <summary>How many bytes of a resumable upload the server holds for a lease.</summary>
internal sealed record UploadOffsetDto(long Bytes);

internal static class WorkerResultEndpoints
{
    private const string SourceHashHeader = "X-Optimisarr-Source-Sha256";
    private const string CandidateHashHeader = "X-Optimisarr-Candidate-Sha256";
    private const string OffsetHeader = "X-Optimisarr-Offset";

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
            var resolved = await ResolveHeldLeaseAsync(leaseId, http, settings, db, cancellationToken);
            if (resolved.Refusal is { } refusal)
            {
                return refusal;
            }

            var (worker, lease, job) = (resolved.Worker!, resolved.Lease!, resolved.Job!);
            if (DeclaredHashes(http, job, worker, out var claimedSource, out var claimedCandidate) is { } refused)
            {
                await db.SaveChangesAsync(cancellationToken);
                return refused;
            }

            if (string.IsNullOrWhiteSpace(lease.OutputExtension))
            {
                return ApiErrors.Conflict("worker.result.contractMissing",
                    "That lease records no output container, so its candidate cannot be named safely.");
            }

            var finalPath = FinalPathFor(environment, job, lease);
            var stagingPath = finalPath + ".partial";

            // Written under a temporary name and hashed on the way in, so a transfer that dies
            // part-way never leaves something that looks like a finished candidate.
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

            return await AcceptAsync(db, worker, lease, job, stagingPath, finalPath, written, actualHash, claimedCandidate, cancellationToken);
        })
        .WithName("DeliverResult")
        // A candidate is a whole film. Kestrel's default 30 MB body cap exists for form posts, and
        // the first real delivery from a Mac hit it; the body streams to disk in bounded chunks
        // above, so lifting the cap here costs no memory. Found by the live work-loop test.
        .WithMetadata(new DisableRequestSizeLimitAttribute())
        .Produces<ResultAcceptedDto>(StatusCodes.Status202Accepted)
        .Produces<ApiError>(StatusCodes.Status401Unauthorized);

        // The resumable form of the same delivery, for a multi-gigabyte candidate over a link
        // that drops. The worker appends in bounded chunks at the offset the server confirms,
        // asks where it got to after a failure, and finishes with the two hashes; the assembled
        // file is then hashed and judged exactly as a single-shot upload is. The staging file is
        // named by lease, so a resumed upload can only ever continue its own transfer.
        app.MapGet("/api/workers/leases/{leaseId:guid}/result/offset", async (
            Guid leaseId,
            HttpRequest http,
            SettingsStore settings,
            OptimisarrDbContext db,
            IHostEnvironment environment,
            CancellationToken cancellationToken) =>
        {
            var resolved = await ResolveHeldLeaseAsync(leaseId, http, settings, db, cancellationToken);
            if (resolved.Refusal is { } refusal)
            {
                return refusal;
            }

            return Results.Ok(new UploadOffsetDto(StagedBytes(StagingPathFor(environment, resolved.Job!, resolved.Lease!))));
        })
        .WithName("ResultUploadOffset")
        .Produces<UploadOffsetDto>()
        .Produces<ApiError>(StatusCodes.Status401Unauthorized);

        app.MapPatch("/api/workers/leases/{leaseId:guid}/result", async (
            Guid leaseId,
            [FromHeader(Name = OffsetHeader)] long? offset,
            HttpRequest http,
            SettingsStore settings,
            OptimisarrDbContext db,
            IHostEnvironment environment,
            CancellationToken cancellationToken) =>
        {
            var resolved = await ResolveHeldLeaseAsync(leaseId, http, settings, db, cancellationToken);
            if (resolved.Refusal is { } refusal)
            {
                return refusal;
            }

            if (offset is null or < 0)
            {
                return ApiErrors.BadRequest("worker.result.offsetMissing",
                    $"A chunk must say where it starts, in the {OffsetHeader} header.");
            }

            var stagingPath = StagingPathFor(environment, resolved.Job!, resolved.Lease!);
            var staged = StagedBytes(stagingPath);
            if (offset.Value != staged)
            {
                // The worker's idea of where it got to and the file's disagree: a chunk was lost
                // in flight, or repeated. Naming the real offset is what lets it resume correctly.
                return Results.Json(
                    new ApiError("worker.result.offsetMismatch",
                        $"The staged upload holds {staged} bytes, not {offset.Value}; resume from {staged}.",
                        new { bytes = staged }),
                    statusCode: StatusCodes.Status409Conflict);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(stagingPath)!);
            long appended;
            try
            {
                appended = await AppendToFileAsync(http.Body, stagingPath, cancellationToken);
            }
            catch (Exception)
            {
                // Whatever landed stays: the next offset query says how much, and the worker
                // resumes from there. That is the whole point of this route.
                throw;
            }

            return Results.Ok(new UploadOffsetDto(staged + appended));
        })
        .WithName("AppendResultChunk")
        .WithMetadata(new DisableRequestSizeLimitAttribute())
        .Produces<UploadOffsetDto>()
        .Produces<ApiError>(StatusCodes.Status400BadRequest)
        .Produces<ApiError>(StatusCodes.Status401Unauthorized)
        .Produces<ApiError>(StatusCodes.Status409Conflict);

        app.MapPost("/api/workers/leases/{leaseId:guid}/result/complete", async (
            Guid leaseId,
            HttpRequest http,
            SettingsStore settings,
            OptimisarrDbContext db,
            IHostEnvironment environment,
            CancellationToken cancellationToken) =>
        {
            var resolved = await ResolveHeldLeaseAsync(leaseId, http, settings, db, cancellationToken);
            if (resolved.Refusal is { } refusal)
            {
                return refusal;
            }

            var (worker, lease, job) = (resolved.Worker!, resolved.Lease!, resolved.Job!);
            if (DeclaredHashes(http, job, worker, out _, out var claimedCandidate) is { } refused)
            {
                await db.SaveChangesAsync(cancellationToken);
                return refused;
            }

            if (string.IsNullOrWhiteSpace(lease.OutputExtension))
            {
                return ApiErrors.Conflict("worker.result.contractMissing",
                    "That lease records no output container, so its candidate cannot be named safely.");
            }

            var stagingPath = StagingPathFor(environment, job, lease);
            if (!File.Exists(stagingPath))
            {
                return ApiErrors.Conflict("worker.result.nothingStaged",
                    "No chunks have been uploaded for this lease, so there is nothing to complete.");
            }

            var (written, actualHash) = await HashFileAsync(stagingPath, cancellationToken);
            return await AcceptAsync(db, worker, lease, job, stagingPath, FinalPathFor(environment, job, lease), written, actualHash, claimedCandidate, cancellationToken);
        })
        .WithName("CompleteResultUpload")
        .Produces<ResultAcceptedDto>(StatusCodes.Status202Accepted)
        .Produces<ApiError>(StatusCodes.Status401Unauthorized)
        .Produces<ApiError>(StatusCodes.Status409Conflict);
    }

    private sealed record ResolvedLease(IResult? Refusal, Worker? Worker, JobLease? Lease, Job? Job);

    /// <summary>
    /// The checks every delivery route shares, in the order chosen for what each protects:
    /// authenticate first, so nothing about a lease is revealed to a caller with no claim on it;
    /// then prove the claim is live, which covers both the late result and the duplicate delivery;
    /// then that the job is still leased, since an operator may have cancelled it meanwhile.
    /// </summary>
    private static async Task<ResolvedLease> ResolveHeldLeaseAsync(
        Guid leaseId,
        HttpRequest http,
        SettingsStore settings,
        OptimisarrDbContext db,
        CancellationToken cancellationToken)
    {
        if (await WorkerGate.RefusedAsync(settings, cancellationToken) is { } refused)
        {
            return new ResolvedLease(refused, null, null, null);
        }

        var worker = await WorkerAuth.ResolveAsync(http, db, cancellationToken);
        if (worker is null)
        {
            return new ResolvedLease(WorkerGate.Unauthenticated(), null, null, null);
        }

        var lease = await db.JobLeases
            .Include(l => l.Job)
            .ThenInclude(job => job!.MediaFile)
            .FirstOrDefaultAsync(l => l.Id == leaseId, cancellationToken);
        if (lease is null)
        {
            return new ResolvedLease(ApiErrors.NotFound("worker.lease.notFound", $"No lease with id {leaseId}."), null, null, null);
        }

        if (lease.WorkerId != worker.Id)
        {
            return new ResolvedLease(Results.Json(
                new ApiError("worker.lease.notHolder", "That lease belongs to another worker."),
                statusCode: StatusCodes.Status403Forbidden), null, null, null);
        }

        if (lease.ToDomain().StateAt(DateTimeOffset.UtcNow) != LeaseState.Held)
        {
            return new ResolvedLease(ApiErrors.Conflict("worker.lease.notHeld",
                "That lease is no longer held, so a result cannot be delivered through it."), null, null, null);
        }

        var job = lease.Job;
        if (job?.MediaFile is null)
        {
            return new ResolvedLease(ApiErrors.NotFound("worker.source.missing", "That lease has no source."), null, null, null);
        }

        // The lease being live is not enough: an operator may have cancelled the job while the
        // worker was still encoding. A candidate for a job that is no longer leased has nowhere
        // to go, and accepting it would quietly revive work someone chose to stop.
        if (job.Status != JobStatus.Leased)
        {
            return new ResolvedLease(ApiErrors.Conflict("worker.result.jobNotLeased",
                $"That job is {job.Status}, not leased, so a result cannot be delivered for it."), null, null, null);
        }

        return new ResolvedLease(null, worker, lease, job);
    }

    /// <summary>
    /// Reads the two declared hashes and proves the source one names this job's source. A
    /// candidate encoded from different bytes is not evidence about this job whatever its quality.
    /// Records the refusal on the worker; the caller saves.
    /// </summary>
    private static IResult? DeclaredHashes(HttpRequest http, Job job, Worker worker, out string claimedSource, out string claimedCandidate)
    {
        claimedSource = http.Headers[SourceHashHeader].ToString();
        claimedCandidate = http.Headers[CandidateHashHeader].ToString();

        if (string.IsNullOrWhiteSpace(claimedSource) || string.IsNullOrWhiteSpace(claimedCandidate))
        {
            // Fail closed on missing evidence rather than accepting an unattributable file.
            return ApiErrors.BadRequest("worker.result.evidenceMissing",
                $"Both {SourceHashHeader} and {CandidateHashHeader} are required.");
        }

        if (!string.Equals(job.SourceSha256, claimedSource, StringComparison.OrdinalIgnoreCase))
        {
            WorkerProblems.Record(worker,
                $"Its candidate for {job.MediaFile!.RelativePath} was encoded from a different source and was refused.",
                DateTimeOffset.UtcNow);
            return ApiErrors.Conflict("worker.result.sourceMismatch",
                "That candidate was encoded from a different source than this job's.");
        }

        return null;
    }

    /// <summary>
    /// The last step of every delivery: the assembled file's real hash must match the one the
    /// worker declared, and only then does the candidate take its final name and the job move on.
    /// </summary>
    private static async Task<IResult> AcceptAsync(
        OptimisarrDbContext db,
        Worker worker,
        JobLease lease,
        Job job,
        string stagingPath,
        string finalPath,
        long written,
        string actualHash,
        string claimedCandidate,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(actualHash, claimedCandidate, StringComparison.OrdinalIgnoreCase))
        {
            // Truncated, corrupted, or misdescribed. Any of those verified as a real candidate
            // could end up replacing an original with a broken file.
            TryDelete(stagingPath);
            WorkerProblems.Record(worker,
                $"Its candidate for {job.MediaFile!.RelativePath} did not match the hash it declared and was refused.",
                DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(cancellationToken);
            return ApiErrors.Conflict("worker.result.hashMismatch",
                "The uploaded candidate does not match the hash the worker declared.");
        }

        TryDelete(finalPath);
        File.Move(stagingPath, finalPath);

        job.WorkOutputPath = finalPath;
        lease.DeliveredSha256 = actualHash;

        // Deliberately not ReadyToReplace. Verification has not run, and a candidate produced
        // elsewhere earns nothing until every local gate has been repeated against it. Nor
        // Verifying: that means verification is running here now, and restart recovery would
        // rightly discard it as interrupted. The dispatcher picks this status up in its turn.
        job.Status = JobStatus.AwaitingVerification;

        lease.Apply(lease.ToDomain().Complete(worker.Id, DateTimeOffset.UtcNow).Lease);
        await db.SaveChangesAsync(cancellationToken);

        // Accepted rather than OK: the candidate is delivered and intact, not yet judged.
        return Results.Accepted(value: new ResultAcceptedDto(job.Id, written, actualHash));
    }

    private static string FinalPathFor(IHostEnvironment environment, Job job, JobLease lease)
    {
        var outputRoot = WorkOutputRoot.ForMediaFile(WorkPaths.Resolve(environment), job.MediaFileId);
        Directory.CreateDirectory(outputRoot);
        return RemoteCandidate.PathFor(outputRoot, job.Id, "." + lease.OutputExtension!.TrimStart('.'));
    }

    /// <summary>Named by lease, so a resumed upload can only continue its own transfer.</summary>
    private static string StagingPathFor(IHostEnvironment environment, Job job, JobLease lease) =>
        Path.Combine(
            WorkOutputRoot.ForMediaFile(WorkPaths.Resolve(environment), job.MediaFileId),
            $"{RemoteCandidate.Prefix}{job.Id}.{lease.Id:N}.partial");

    private static long StagedBytes(string stagingPath) =>
        File.Exists(stagingPath) ? new FileInfo(stagingPath).Length : 0;

    private static async Task<long> AppendToFileAsync(Stream body, string path, CancellationToken cancellationToken)
    {
        var buffer = new byte[128 * 1024];
        long total = 0;
        await using var file = new FileStream(
            path, FileMode.Append, FileAccess.Write, FileShare.None, buffer.Length, useAsync: true);
        int read;
        while ((read = await body.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            total += read;
        }

        return total;
    }

    private static async Task<(long Bytes, string Sha256)> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true);
        var hash = await SHA256.HashDataAsync(file, cancellationToken);
        return (file.Length, Convert.ToHexString(hash).ToLowerInvariant());
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
