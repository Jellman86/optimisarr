using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Library;
using Optimisarr.Api.Deduplication;
using Optimisarr.Api.Queue;
using Optimisarr.Api.Workers;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Workers;
using Optimisarr.Data;

namespace Optimisarr.Api.Endpoints;

internal sealed record ResultAcceptedDto(int JobId, long Bytes, string CandidateSha256);

/// <summary>How many bytes of a resumable upload the server holds for a lease.</summary>
internal sealed record UploadOffsetDto(long Bytes);

internal static partial class WorkerResultEndpoints
{
    private const string SourceHashHeader = "X-Optimisarr-Source-Sha256";
    private const string CandidateHashHeader = "X-Optimisarr-Candidate-Sha256";
    private const string OffsetHeader = "X-Optimisarr-Offset";

    public static void MapWorkerResultEndpoints(this WebApplication app)
    {
        app.MapPost("/api/workers/leases/{leaseId:guid}/verification", async (
            Guid leaseId, RemoteVerificationEvidence request, HttpRequest http,
            SettingsStore settings, OptimisarrDbContext db, CancellationToken cancellationToken) =>
        {
            var resolved = await ResolveHeldLeaseAsync(leaseId, http, settings, db, cancellationToken);
            if (resolved.Refusal is { } refusal) return refusal;
            var lease = resolved.Lease!;
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var contract = lease.VerificationContractJson is { } json
                ? JsonSerializer.Deserialize<RemoteVerificationContract>(json, options) : null;
            if (contract is null || request.ContractId != contract.Id)
                return ApiErrors.Conflict("worker.verification.contractMismatch", "This lease did not request that verification contract.");
            if (!string.Equals(request.SourceSha256, resolved.Job!.SourceSha256, StringComparison.OrdinalIgnoreCase)
                || request.CandidateSha256 is not { Length: 64 } hash || !hash.All(Uri.IsHexDigit))
                return ApiErrors.Conflict("worker.verification.hashMismatch", "Verification must identify the source and candidate hashes.");
            // Store negative/incomplete measurements too: strict verification will fail the job
            // with their reason, never silently fall back or repeatedly requeue an incapable worker.
            var evidence = JsonSerializer.Serialize(request, options);
            // Compare-and-set in the database: overlapping retries must not overwrite the first
            // report, even when both requests read the lease before either saves it.
            await using var write = await db.BeginDiagnosticWriteAsync(cancellationToken);
            var written = await db.JobLeases
                .Where(row => row.Id == lease.Id && row.State == LeaseState.Held && row.VerificationEvidenceJson == null)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.VerificationEvidenceJson, evidence), cancellationToken);
            if (written == 0)
            {
                var previous = await db.JobLeases.AsNoTracking().Where(row => row.Id == lease.Id)
                    .Select(row => row.VerificationEvidenceJson).SingleAsync(cancellationToken);
                if (previous != evidence)
                    return ApiErrors.Conflict("worker.verification.alreadyRecorded", "Different evidence is already recorded or the lease has ended.");
            }
            else
            {
                await db.Entry(lease).ReloadAsync(cancellationToken);
                await DiagnosticEventCapture.AppendLeaseAsync(db, lease, DateTimeOffset.UtcNow, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
            }
            await write.CommitAsync(cancellationToken);
            return Results.Ok(new { leaseId });
        })
        .WithName("ReportWorkerVerification")
        .WithMetadata(new RequestSizeLimitAttribute(3 * 1024 * 1024));

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
            FileOperationClaims claims,
            IHostEnvironment environment,
            CancellationToken cancellationToken) =>
        {
            using var uploadLock = await LeaseUploadLock.AcquireAsync(leaseId, cancellationToken);
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

            var snapshot = UploadSnapshot.Capture(environment, job, lease);
            var start = await StartUploadAsync(db, claims, snapshot, http, settings, singleShot: true, 0, cancellationToken);
            if (start.Refusal is { } startRefusal) return startRefusal;
            var upload = start.Upload!;
            try
            {
                var streamed = await StreamUploadAsync(db, claims, snapshot, upload, http, settings, cancellationToken);
                if (streamed.Refusal is { } streamRefusal) return streamRefusal;
                var sealedUpload = await SealUploadAsync(db, claims, snapshot, http, settings, upload, streamed.Bytes, cancellationToken);
                if (sealedUpload.Refusal is { } sealRefusal) return sealRefusal;
                return await AcceptAsync(db, claims, worker, lease, job, upload.Path, snapshot.FinalPath,
                    streamed.Bytes, streamed.Hash!, claimedCandidate, http, settings, cancellationToken, upload.Generation, snapshot.Binding);
            }
            catch
            {
                await CleanupInterruptedUploadAsync(db, claims, snapshot, upload);
                throw;
            }

        })
        .WithName("DeliverResult")
        // A candidate is a whole film. Kestrel's default 30 MB body cap exists for form posts, and
        // the first real delivery from a Mac hit it; the body streams to disk in bounded chunks
        // above, so lifting the cap here keeps memory bounded. Found by the live work-loop test.
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
            using var uploadLock = await LeaseUploadLock.AcquireAsync(leaseId, cancellationToken);
            var resolved = await ResolveHeldLeaseAsync(leaseId, http, settings, db, cancellationToken);
            if (resolved.Refusal is { } refusal)
            {
                return refusal;
            }

            var snapshot = UploadSnapshot.Capture(environment, resolved.Job!, resolved.Lease!);
            var path = resolved.Lease!.ResultUploadPath ?? snapshot.StagingPath;
            if (!snapshot.Owns(path) || resolved.Lease.ResultUploadGeneration is not null
                && resolved.Lease.ResultUploadBinding != snapshot.Binding) return UploadChanged();
            var readable = File.Exists(path) ? path : resolved.Lease.ResultUploadSealedBytes is not null
                && File.Exists(snapshot.FinalPath) ? snapshot.FinalPath : null;
            var bytes = readable is null ? 0 : StagedBytes(readable);
            if (resolved.Lease.ResultUploadSealedBytes is { } sealedBytes && (readable is null || bytes != sealedBytes))
                return UploadChanged();
            return Results.Ok(new UploadOffsetDto(bytes));
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
            FileOperationClaims claims,
            IHostEnvironment environment,
            CancellationToken cancellationToken) =>
        {
            using var uploadLock = await LeaseUploadLock.AcquireAsync(leaseId, cancellationToken);
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

            var snapshot = UploadSnapshot.Capture(environment, resolved.Job!, resolved.Lease!);
            var start = await StartUploadAsync(db, claims, snapshot, http, settings, singleShot: false, offset.Value, cancellationToken);
            if (start.Refusal is { } startRefusal) return startRefusal;
            var streamed = await StreamUploadAsync(db, claims, snapshot, start.Upload!, http, settings, cancellationToken);
            return streamed.Refusal ?? Results.Ok(new UploadOffsetDto(streamed.Bytes));

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
            FileOperationClaims claims,
            IHostEnvironment environment,
            CancellationToken cancellationToken) =>
        {
            using var uploadLock = await LeaseUploadLock.AcquireAsync(leaseId, cancellationToken);
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

            var snapshot = UploadSnapshot.Capture(environment, job, lease);
            var seal = await SealUploadAsync(db, claims, snapshot, http, settings, null, null, cancellationToken);
            if (seal.Refusal is { } sealRefusal) return sealRefusal;
            var upload = seal.Upload!;
            var hashPath = File.Exists(upload.Path) ? upload.Path : snapshot.FinalPath;
            var (written, actualHash) = await HashFileAsync(hashPath, cancellationToken);
            return await AcceptAsync(db, claims, worker, lease, job, upload.Path, snapshot.FinalPath,
                written, actualHash, claimedCandidate, http, settings, cancellationToken, upload.Generation, snapshot.Binding);

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
        CancellationToken cancellationToken, bool recordSeen = true)
    {
        if (await WorkerGate.RefusedAsync(settings, cancellationToken) is { } refused)
        {
            return new ResolvedLease(refused, null, null, null);
        }

        var worker = await WorkerAuth.ResolveAsync(http, db, cancellationToken, recordSeen);
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

        if (lease.ExecutionAttempt != job.ExecutionAttempt)
            return new ResolvedLease(ApiErrors.Conflict("worker.result.attemptEnded",
                "This lease belongs to an earlier execution attempt."), null, null, null);

        if (lease.PerceptualSearchStateJson is { } perceptualJson)
        {
            try
            {
                var search = JsonSerializer.Deserialize<RemotePerceptualSearchState>(perceptualJson, PerceptualWorkerWork.Json);
                if (search?.Encoding is null || !search.CanEncode
                    || !string.Equals(search.SourceSha256, job.SourceSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Unsettled or unbound search.");
                // Validate the frozen command and final verification intent too. A corrupt
                // encoding snapshot cannot borrow an otherwise complete search's permission.
                _ = PerceptualWorkerWork.Complete(lease.VerificationWorkJson, search, PerceptualWorkerWork.Summary(search));
            }
            catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
            {
                return new ResolvedLease(ApiErrors.Conflict("worker.perceptual.notSettled",
                    "The assigned perceptual search and frozen encode must settle before verification or candidate delivery."), null, null, null);
            }
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
        FileOperationClaims claims,
        Worker worker,
        JobLease lease,
        Job job,
        string stagingPath,
        string finalPath,
        long written,
        string actualHash,
        string claimedCandidate,
        HttpRequest http,
        SettingsStore settings,
        CancellationToken cancellationToken, Guid generation, string binding)
    {
        // A crash can leave a promoted file with an uncommitted lease. Hash that file before
        // reserving the writer as well. The persisted generation/seal excludes appends across coordinators.
        var existingMatches = false;
        if (File.Exists(finalPath))
        {
            var (existingBytes, existingHash) = await HashFileAsync(finalPath, cancellationToken);
            existingMatches = existingBytes == written && string.Equals(existingHash, actualHash, StringComparison.OrdinalIgnoreCase);
        }

        // SQLite's non-deferred transaction reserves the writer before re-reading. Cancellation,
        // revocation, reassignment and renewals serialize with this short acceptance transaction;
        // no database lock is held while streaming or hashing the upload.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.ChangeTracker.Clear();
        var persisted = await db.Jobs.AsNoTracking().Where(j => j.Id == job.Id)
            .Select(j => new { Path = j.MediaFile!.Path, j.WorkOutputPath }).SingleOrDefaultAsync(cancellationToken);
        using var held = await FileMutationGuard.TryAcquireAsync(db, claims, job.MediaFileId,
            new[] { stagingPath, finalPath, job.MediaFile!.Path, job.WorkOutputPath, persisted?.Path, persisted?.WorkOutputPath }
                .OfType<string>().ToArray(), cancellationToken);
        if (held is null)
            return ApiErrors.Conflict("dedupe.reserved",
                "This file is reserved by another operation. Resolve or finish that operation before delivering it.");

        var current = await ResolveHeldLeaseAsync(lease.Id, http, settings, db, cancellationToken);
        if (current.Refusal is { } refusal)
        {
            return refusal;
        }
        var currentLease = current.Lease!;
        var currentJob = current.Job!;
        if (currentLease.ResultUploadGeneration != generation || currentLease.ResultUploadPath != stagingPath
            || currentLease.ResultUploadBinding != binding
            || currentLease.ResultUploadSealedBytes != written
            || (File.Exists(stagingPath) ? new FileInfo(stagingPath).Length : File.Exists(finalPath) ? new FileInfo(finalPath).Length : -1) != written)
            return UploadChanged();
        var completedAt = DateTimeOffset.UtcNow;
        var completion = currentLease.ToDomain().Complete(worker.Id, completedAt);
        if (completion.Outcome != LeaseOutcome.Completed
            || currentJob.MediaFileId != job.MediaFileId
            || currentJob.ExecutionAttempt != job.ExecutionAttempt
            || currentLease.ExecutionAttempt != lease.ExecutionAttempt
            || currentJob.MediaFile!.Path != job.MediaFile!.Path
            || currentJob.WorkOutputPath != job.WorkOutputPath
            || currentLease.OutputExtension != lease.OutputExtension
            || !string.Equals(currentJob.SourceSha256, job.SourceSha256, StringComparison.OrdinalIgnoreCase))
        {
            return ApiErrors.Conflict("worker.result.attemptEnded", "This assignment ended during delivery; the candidate was not accepted.");
        }

        if (!string.Equals(actualHash, claimedCandidate, StringComparison.OrdinalIgnoreCase))
        {
            // Truncated, corrupted, or misdescribed. Any of those verified as a real candidate
            // could end up replacing an original with a broken file.
            var removed = TryDelete(stagingPath);
            WorkerProblems.Record(current.Worker!,
                $"Its candidate for {job.MediaFile!.RelativePath} did not match the hash it declared and was refused.",
                DateTimeOffset.UtcNow);
            if (removed) ClearUpload(currentLease);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return ApiErrors.Conflict("worker.result.hashMismatch",
                "The uploaded candidate does not match the hash the worker declared.");
        }

        if (File.Exists(finalPath))
        {
            if (!existingMatches)
                return ApiErrors.Conflict("worker.result.candidateExists", "A different candidate already occupies this assignment's path.");
            TryDelete(stagingPath);
        }
        else File.Move(stagingPath, finalPath);
        // The persisted seal survives promotion. If commit fails or the process exits here, retry
        // hashes the final file and finishes this same generation without overwriting anything.
        currentJob.WorkOutputPath = finalPath;
        currentJob.Status = JobStatus.AwaitingVerification;
        currentLease.DeliveredSha256 = actualHash;
        currentLease.Apply(completion.Lease, completedAt);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Results.Accepted(value: new ResultAcceptedDto(currentJob.Id, written, actualHash));
    }

    private static string FinalPathFor(IHostEnvironment environment, Job job, JobLease lease)
    {
        var outputRoot = WorkOutputRoot.ForMediaFile(WorkPaths.Resolve(environment), job.MediaFileId);
        return RemoteCandidate.PathFor(outputRoot, job.Id, $".{lease.Id:N}." + (lease.OutputExtension ?? "candidate").TrimStart('.'));
    }

    /// <summary>Named by lease, so a resumed upload can only continue its own transfer.</summary>
    private static string StagingPathFor(IHostEnvironment environment, Job job, JobLease lease) =>
        Path.Combine(
            WorkOutputRoot.ForMediaFile(WorkPaths.Resolve(environment), job.MediaFileId),
            $"{RemoteCandidate.Prefix}{job.Id}.{lease.Id:N}.partial");

    private static long StagedBytes(string stagingPath) =>
        File.Exists(stagingPath) ? new FileInfo(stagingPath).Length : 0;

    private static async Task<(long Bytes, string Sha256)> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long bytes = 0;
        int read;
        while ((read = await file.ReadAsync(buffer, cancellationToken)) > 0)
        {
            hash.AppendData(buffer.AsSpan(0, read));
            bytes += read;
        }
        return (bytes, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Keep its seal when removal fails, so later appends cannot reuse unverified bytes.
            return false;
        }
    }
}
