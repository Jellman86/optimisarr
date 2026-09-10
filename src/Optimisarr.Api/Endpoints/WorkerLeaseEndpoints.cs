using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Library;
using Optimisarr.Api.Queue;
using Optimisarr.Api.Realtime;
using Optimisarr.Api.Workers;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Verification;
using Optimisarr.Core.Workers;
using Optimisarr.Data;

namespace Optimisarr.Api.Endpoints;

/// <summary>
/// One job handed to one worker, with the deadline by which it must renew or lose it.
/// The encode policy is resolved here rather than by the worker: the control plane owns the rules,
/// and <see cref="Arguments"/> is the exact FFmpeg command this machine would have run for the
/// worker's encoder. Two tokens stand in for paths — <see cref="WorkerProtocol.InputPlaceholder"/>
/// for the worker's copy of the source and <see cref="WorkerProtocol.OutputPlaceholder"/>, carrying
/// <see cref="OutputExtension"/>, for its candidate. No path on this machine is ever sent: the
/// source is fetched by lease, and a worker substitutes only its own scratch paths.
/// </summary>
internal sealed record AssignmentDto(
    Guid LeaseId,
    int JobId,
    long SourceBytes,
    string VideoEncoder,
    string Vmaf,
    DateTimeOffset ExpiresUtc,
    int RenewWithinSeconds,
    IReadOnlyList<string> Arguments,
    string OutputExtension,
    QualityRequirementDto Quality);

/// <summary>
/// What the worker's VMAF evidence will be held to. The thresholds and model are stated so the
/// worker measures against the policy this machine will judge by, and its evidence can be bound to
/// them; a score taken under an easier policy or another model is evidence about something else.
/// </summary>
internal sealed record QualityRequirementDto(
    bool Measure,
    string Model,
    int FrameSubsample,
    bool ClipVmaf,
    double MinimumHarmonicMean,
    double MinimumMinimum,
    /// <summary>
    /// The server's own libvmaf command per measurement window, with <c>{{distorted}}</c>,
    /// <c>{{reference}}</c> and <c>{{log}}</c> standing in for the worker's paths. Empty when nothing
    /// is to be measured. The worker returns the raw JSON logs in this order.
    /// </summary>
    IReadOnlyList<IReadOnlyList<string>> Commands,
    /// <summary>How the windows sample the file, for the report.</summary>
    string Sampling);

/// <summary>The libvmaf logs a worker returns, one per command it was sent, bound to both hashes.</summary>
internal sealed record QualityEvidenceRequest(
    string SourceSha256,
    string CandidateSha256,
    IReadOnlyList<string> Logs);

/// <summary>The pooled scores the server read from those logs.</summary>
internal sealed record QualityEvidenceAcceptedDto(
    Guid LeaseId,
    double? VmafHarmonicMean,
    double? VmafFifthPercentile,
    double? VmafMin,
    int? FrameCount);

internal sealed record LeaseRenewedDto(Guid LeaseId, DateTimeOffset ExpiresUtc);

/// <summary>
/// What a worker may say about a job when it renews. Both optional: an older sidecar renews with
/// no body and the claim is simply extended. Stage is a name from <see cref="RemoteStage"/>;
/// encoded seconds is ffmpeg's own out_time, which the server scales against the source duration.
/// </summary>
internal sealed record RenewRequest(string? Stage = null, double? EncodedSeconds = null);

internal static class WorkerLeaseEndpoints
{
    private static readonly JsonSerializerOptions EvidenceJson = new(JsonSerializerDefaults.Web);

    public static void MapWorkerLeaseEndpoints(this WebApplication app)
    {
        // A worker asking for something to do. 204 when there is nothing it can run, which is the
        // ordinary answer most of the time and not an error.
        app.MapPost("/api/workers/claim", async (
            HttpRequest http,
            SettingsStore settings,
            OptimisarrDbContext db,
            QueueDispatcher dispatcher,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var logger = loggerFactory.CreateLogger("Optimisarr.Api.Workers.Claim");
            if (await WorkerGate.RefusedAsync(settings, cancellationToken) is { } refused)
            {
                return refused;
            }

            var worker = await WorkerAuth.ResolveAsync(http, db, cancellationToken);
            if (worker is null)
            {
                return WorkerGate.Unauthenticated();
            }

            var now = DateTimeOffset.UtcNow;

            // Reclaim before offering. A job whose holder went away must come back to the queue,
            // and doing it here means no separate sweeper has to be running for work to recover.
            await ReclaimExpiredAsync(db, now, cancellationToken);

            // A worker that has stopped checking in is not given new work: it may be mid-shutdown,
            // and a job handed over now would only sit until the lease lapsed.
            if (!WorkerLiveness.IsOnline(worker.LastSeenAt, now) || worker.RevokedAt is not null)
            {
                return Results.NoContent();
            }

            // An operator asked this worker to finish what it holds and take no more. Its renewals
            // and deliveries are untouched; only new offers stop.
            if (worker.DrainRequestedAt is not null)
            {
                return Results.NoContent();
            }

            var held = await db.JobLeases
                .CountAsync(lease => lease.WorkerId == worker.Id && lease.State == LeaseState.Held, cancellationToken);
            if (held >= worker.MaxConcurrency)
            {
                return Results.NoContent();
            }

            var capabilities = worker.ToCapabilities();

            // Ordered the same way the local dispatcher orders its own work, so a remote worker
            // takes the job that would have run next rather than cherry-picking the easy ones.
            //
            // Ordering happens in memory because SQLite cannot ORDER BY a DateTimeOffset — the same
            // constraint the dispatcher and the job date filters already work around. A light
            // projection is ordered first so only the few jobs actually under consideration are
            // loaded with their media file, rather than pulling a whole queue's worth of rows.
            var queuedOrder = await db.Jobs
                .AsNoTracking()
                .Where(job => job.Status == JobStatus.Queued && job.Type == JobType.Normal)
                .Select(job => new { job.Id, job.Priority, job.EnqueuedAt })
                .ToListAsync(cancellationToken);

            var shortlist = queuedOrder
                .OrderBy(job => job.Priority)
                .ThenBy(job => job.EnqueuedAt)
                .Take(25)
                .Select(job => job.Id)
                .ToList();

            if (shortlist.Count == 0)
            {
                return Results.NoContent();
            }

            var loaded = await db.Jobs
                .Include(job => job.MediaFile)!
                .ThenInclude(file => file!.Library)
                .Where(job => shortlist.Contains(job.Id))
                .ToListAsync(cancellationToken);

            var candidates = shortlist
                .Select(id => loaded.FirstOrDefault(job => job.Id == id))
                .Where(job => job is not null)
                .Select(job => job!)
                .ToList();

            foreach (var job in candidates)
            {
                if (job.MediaFile is null)
                {
                    continue;
                }

                // Workers poll, and full preparation probes the source. Rule out the jobs that
                // preparation would refuse anyway from what is already loaded, so a queue of
                // adaptive-library or remux jobs does not cost an ffprobe per candidate per poll.
                // Preparation repeats these refusals itself; this is only the cheap first pass.
                if (!PlausiblyOfferable(job, capabilities))
                {
                    continue;
                }

                // The same preparation local dispatch runs, with the encoder chosen from what this
                // worker proved. A refusal is ordinary — an adaptive library, a remux, an encoder
                // the worker lacks — and is logged rather than surfaced, since the worker's answer
                // is simply the next candidate or nothing.
                var plan = await dispatcher.PrepareRemoteWorkAsync(job.Id, capabilities, cancellationToken);
                if (plan.Assignment is not { } assignment)
                {
                    logger.LogDebug(
                        "Job {JobId} not offered to worker {Worker}: {Reason}",
                        job.Id, worker.Name, plan.Reason);
                    continue;
                }

                var requirements = new JobRequirements(
                    VideoEncoder: assignment.VideoEncoder,
                    // Named only when the command actually uses one, and then it must be proved.
                    HardwareDecoder: assignment.HardwareDecoder,
                    // Only a job whose policy will judge VMAF needs a worker that can score it.
                    Vmaf: assignment.Verification.QualityGateEnabled ? VmafCapability.Cpu : VmafCapability.None,
                    // Scratch for the candidate plus headroom; a worker that cannot hold the output
                    // has no business starting the encode.
                    ScratchBytes: job.MediaFile.SizeBytes + (job.MediaFile.SizeBytes / 2));

                var match = WorkerCapabilityMatcher.Match(capabilities, requirements);
                if (!match.Accepted)
                {
                    logger.LogDebug(
                        "Job {JobId} not offered to worker {Worker}: {Reasons}",
                        job.Id, worker.Name, string.Join(" ", match.Reasons));
                    continue;
                }

                var lease = WorkerLease.Acquire(Guid.NewGuid(), job.Id, worker.Id, now);

                db.JobLeases.Add(new JobLease
                {
                    Id = lease.Id,
                    JobId = lease.JobId,
                    WorkerId = lease.WorkerId,
                    AcquiredAt = lease.AcquiredUtc,
                    ExpiresAt = lease.ExpiresUtc,
                    State = lease.State,
                    // Bound to the lease so delivery names the candidate by the contract, not by
                    // the source; the replacement's final extension comes from that name.
                    OutputExtension = assignment.OutputExtension,
                    HardwareDecoder = assignment.HardwareDecoder,
                    // What the worker was asked to measure, fixed now so the evidence it returns is
                    // judged against this, not against a policy that may have changed since.
                    QualityContractJson = assignment.Quality is null
                        ? null
                        : JsonSerializer.Serialize(assignment.Quality, EvidenceJson),
                });

                // The exclusion that matters: off the queue, so this machine will not also run it.
                job.Status = JobStatus.Leased;

                try
                {
                    await db.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateException)
                {
                    // Another worker claimed it in the gap. The unique index on held leases is what
                    // makes that a database error rather than two holders, so move on and try the
                    // next candidate rather than treating it as a failure.
                    db.ChangeTracker.Clear();
                    continue;
                }

                var policy = assignment.Verification;
                return Results.Ok(new AssignmentDto(
                    lease.Id,
                    job.Id,
                    job.MediaFile.SizeBytes,
                    assignment.VideoEncoder,
                    requirements.Vmaf.ToString(),
                    lease.ExpiresUtc,
                    (int)WorkerLiveness.HeartbeatInterval.TotalSeconds,
                    assignment.Arguments,
                    assignment.OutputExtension,
                    new QualityRequirementDto(
                        policy.QualityGateEnabled,
                        assignment.VmafModel,
                        policy.VmafFrameSubsample,
                        policy.ClipVmafEnabled,
                        policy.MinimumVmafHarmonicMean,
                        policy.MinimumVmafMin,
                        assignment.Quality?.Commands ?? [],
                        assignment.Quality?.Sampling ?? "None")));
            }

            return Results.NoContent();
        })
        .WithName("ClaimWork")
        .Produces<AssignmentDto>()
        .Produces<ApiError>(StatusCodes.Status401Unauthorized);

        app.MapPost("/api/workers/leases/{leaseId:guid}/renew", async (
            Guid leaseId,
            RenewRequest? request,
            HttpRequest http,
            SettingsStore settings,
            OptimisarrDbContext db,
            IHubContext<JobsHub> hub,
            CancellationToken cancellationToken) =>
        {
            RemoteStage? stage = null;
            if (!string.IsNullOrWhiteSpace(request?.Stage))
            {
                // Names are the contract. An unknown one is refused rather than dropped, so a
                // sidecar built against a newer stage list finds out at once instead of showing a
                // job that never seems to move.
                if (!Enum.TryParse<RemoteStage>(request.Stage, ignoreCase: true, out var parsed)
                    || !Enum.IsDefined(parsed))
                {
                    return ApiErrors.BadRequest("worker.lease.stageInvalid",
                        $"Unknown stage: {request.Stage}. Expected one of {string.Join(", ", Enum.GetNames<RemoteStage>())}.");
                }

                stage = parsed;
            }

            (int JobId, double Progress)? report = null;
            var result = await MutateLeaseAsync(leaseId, http, settings, db, cancellationToken,
                (lease, workerId, now) => lease.Renew(workerId, now),
                (stored, job, outcome) =>
                {
                    // A renewal changes nothing about who owns the job; it may say where the
                    // worker has got to, which is what the queue and the Workers tab show.
                    if (stage is { } reported)
                    {
                        stored.Stage = reported;
                    }

                    if (request?.EncodedSeconds is { } encoded && encoded >= 0)
                    {
                        stored.EncodedSeconds = encoded;
                        if (job.MediaFile?.DurationSeconds is { } duration && duration > 0)
                        {
                            // Held under 100% until the candidate is actually delivered, the same
                            // convention the local encode uses so a bar never sits at "done".
                            job.Progress = Math.Clamp(encoded / duration, 0, 0.99);
                            report = (job.Id, job.Progress);
                        }
                    }
                },
                lease => Results.Ok(new LeaseRenewedDto(lease.Id, lease.ExpiresUtc)));

            if (report is { } progress)
            {
                await hub.Clients.All.SendAsync("jobProgress", new
                {
                    jobId = progress.JobId,
                    progress = progress.Progress,
                    fps = (double?)null,
                    speed = (double?)null,
                    etaSeconds = (double?)null,
                    finishing = false,
                }, cancellationToken);
            }

            return result;
        })
        .WithName("RenewLease")
        .Produces<LeaseRenewedDto>()
        .Produces<ApiError>(StatusCodes.Status400BadRequest)
        .Produces<ApiError>(StatusCodes.Status401Unauthorized);

        // The worker's libvmaf logs, delivered before the candidate. Every number is parsed and
        // pooled here by the same code that reads a local measurement; the worker sends only what
        // ffmpeg wrote. Accepted logs are bound to the hashes the worker declares, and are used
        // only if the candidate it then delivers carries the same hash.
        app.MapPost("/api/workers/leases/{leaseId:guid}/quality", async (
            Guid leaseId,
            QualityEvidenceRequest request,
            HttpRequest http,
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

            var now = DateTimeOffset.UtcNow;
            if (lease.ToDomain().StateAt(now) != LeaseState.Held)
            {
                return ApiErrors.Conflict("worker.lease.notHeld", "That lease is no longer held.");
            }

            var contract = lease.QualityContractJson is { } contractJson
                ? JsonSerializer.Deserialize<RemoteQualityContract>(contractJson, EvidenceJson)
                : null;
            if (contract is null)
            {
                return ApiErrors.Conflict("worker.quality.notRequested",
                    "This lease asked for no quality measurement, so none can be reported.");
            }

            if (request.Logs is null || request.Logs.Count != contract.WindowCount)
            {
                return ApiErrors.BadRequest("worker.quality.windowCount",
                    $"Expected {contract.WindowCount} libvmaf log(s), one per command sent, but received {request.Logs?.Count ?? 0}.");
            }

            var relativePath = lease.Job?.MediaFile?.RelativePath ?? $"job {lease.JobId}";
            if (!string.Equals(lease.Job?.SourceSha256, request.SourceSha256, StringComparison.OrdinalIgnoreCase))
            {
                WorkerProblems.Record(worker,
                    $"Its quality evidence for {relativePath} was measured against a different source and was refused.",
                    now);
                await db.SaveChangesAsync(cancellationToken);
                return ApiErrors.Conflict("worker.quality.sourceMismatch",
                    "That evidence was measured against a different source than this job's.");
            }

            var windows = new List<QualityResult>(contract.WindowCount);
            foreach (var log in request.Logs)
            {
                var parsed = string.IsNullOrWhiteSpace(log) ? null : QualityScoreParser.Parse(log);
                if (parsed is null)
                {
                    return ApiErrors.BadRequest("worker.quality.logInvalid",
                        "A returned log is not a libvmaf JSON log with pooled VMAF metrics.");
                }

                windows.Add(QualityResult.Ok(parsed with { ModelVersion = contract.Model }));
            }

            var pooled = QualityScoreAggregator.Combine(windows, contract.Sampling);
            if (!pooled.Measured || pooled.Scores is null)
            {
                return ApiErrors.BadRequest("worker.quality.logInvalid",
                    pooled.Error ?? "The returned logs produced no usable score.");
            }

            lease.QualityScoresJson = JsonSerializer.Serialize(pooled.Scores, EvidenceJson);
            lease.QualitySourceSha256 = request.SourceSha256;
            lease.QualityCandidateSha256 = request.CandidateSha256;
            await db.SaveChangesAsync(cancellationToken);

            return Results.Ok(new QualityEvidenceAcceptedDto(
                lease.Id,
                pooled.Scores.VmafHarmonicMean,
                pooled.Scores.VmafFifthPercentile,
                pooled.Scores.VmafMin,
                pooled.Scores.FrameCount));
        })
        .WithName("ReportQualityEvidence")
        .Produces<QualityEvidenceAcceptedDto>()
        .Produces<ApiError>(StatusCodes.Status400BadRequest)
        .Produces<ApiError>(StatusCodes.Status401Unauthorized)
        .Produces<ApiError>(StatusCodes.Status403Forbidden)
        .Produces<ApiError>(StatusCodes.Status409Conflict);

        app.MapPost("/api/workers/leases/{leaseId:guid}/release", async (
            Guid leaseId,
            HttpRequest http,
            SettingsStore settings,
            OptimisarrDbContext db,
            CancellationToken cancellationToken) =>
            await MutateLeaseAsync(leaseId, http, settings, db, cancellationToken,
                (lease, workerId, now) => lease.Release(workerId, now),
                (_, job, outcome) =>
                {
                    // Giving a job up must never strand it, so it goes straight back on the queue
                    // for this machine or another worker to pick up.
                    job.Status = JobStatus.Queued;
                },
                _ => Results.NoContent()))
        .WithName("ReleaseLease")
        // Declared like the other worker routes: this is outside admin-token protection, so the
        // document transformer will not add the 401 that an unknown or revoked credential returns.
        .Produces<ApiError>(StatusCodes.Status401Unauthorized);
    }

    /// <summary>
    /// The shared shape of renew and release: authenticate, load, run the domain state machine, and
    /// persist only if it accepted. Keeping one path means neither operation can skip the ownership
    /// check or forget to re-derive expiry.
    /// </summary>
    private static async Task<IResult> MutateLeaseAsync(
        Guid leaseId,
        HttpRequest http,
        SettingsStore settings,
        OptimisarrDbContext db,
        CancellationToken cancellationToken,
        Func<WorkerLease, int, DateTimeOffset, LeaseResult> operation,
        Action<JobLease, Job, LeaseOutcome> applyToJob,
        Func<WorkerLease, IResult> success)
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

        var stored = await db.JobLeases
            .Include(lease => lease.Job)
            .ThenInclude(job => job!.MediaFile)
            .FirstOrDefaultAsync(lease => lease.Id == leaseId, cancellationToken);

        if (stored is null)
        {
            return ApiErrors.NotFound("worker.lease.notFound", $"No lease with id {leaseId}.");
        }

        var now = DateTimeOffset.UtcNow;
        var result = operation(stored.ToDomain(), worker.Id, now);

        switch (result.Outcome)
        {
            case LeaseOutcome.NotHolder:
                // Deliberately 403 rather than 404: the lease exists, this worker simply does not
                // hold it. Pretending it is missing would make a genuine bug harder to diagnose.
                return Results.Json(
                    new ApiError("worker.lease.notHolder", "That lease belongs to another worker."),
                    statusCode: StatusCodes.Status403Forbidden);

            case LeaseOutcome.Expired:
                return ApiErrors.Conflict("worker.lease.expired",
                    "That lease has expired and the job may have been reassigned.");

            case LeaseOutcome.NotHeld:
                return ApiErrors.Conflict("worker.lease.notHeld", "That lease is no longer held.");
        }

        stored.Apply(result.Lease);
        if (stored.Job is not null)
        {
            applyToJob(stored, stored.Job, result.Outcome);
        }

        await db.SaveChangesAsync(cancellationToken);
        return success(result.Lease);
    }

    /// <summary>
    /// Returns jobs whose holders went silent. Run whenever a worker asks for work, so recovery
    /// needs no background sweeper — the queue heals on the next thing that would have used it.
    /// </summary>
    private static async Task ReclaimExpiredAsync(
        OptimisarrDbContext db,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // The state filter runs in the database; the expiry comparison does not. SQLite cannot
        // order or compare a DateTimeOffset, which is the same reason the job date filters evaluate
        // in memory. Held leases are few, so pulling them and filtering here is cheap.
        var held = await db.JobLeases
            .Include(lease => lease.Job)
            .ThenInclude(job => job!.MediaFile)
            .Include(lease => lease.Worker)
            .Where(lease => lease.State == LeaseState.Held)
            .ToListAsync(cancellationToken);

        var lapsed = held.Where(lease => lease.ExpiresAt <= now).ToList();

        if (lapsed.Count == 0)
        {
            return;
        }

        foreach (var lease in lapsed)
        {
            lease.State = LeaseState.Expired;

            // The worker may never learn its lease lapsed — it went quiet, which is the whole
            // reason — so the operator is told instead, on the worker's own card.
            if (lease.Worker is { } holder)
            {
                WorkerProblems.Record(
                    holder,
                    $"Its lease on {lease.Job?.MediaFile?.RelativePath ?? $"job {lease.JobId}"} lapsed "
                    + $"after {WorkerLiveness.OfflineAfter.TotalMinutes:0} minutes of silence; the job went back to the queue.",
                    now);
            }

            // Only a job still sitting in Leased is ours to hand back. One that moved on — because
            // an operator cancelled it, say — must not be dragged back onto the queue.
            if (lease.Job is { Status: JobStatus.Leased } job)
            {
                job.Status = JobStatus.Queued;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// The refusals that need nothing beyond the loaded rows: only a video re-encode is offered,
    /// an adaptive library's job waits for its per-title quality, and the worker must prove an
    /// encoder for the target codec. Mirrors <see cref="QueueDispatcher.PrepareRemoteWorkAsync"/>,
    /// which remains the authority.
    /// </summary>
    private static bool PlausiblyOfferable(Job job, WorkerCapabilities capabilities)
    {
        var media = job.MediaFile!;
        if (media.MediaKind is MediaKind.Audio or MediaKind.Image)
        {
            return false;
        }

        var library = media.Library;
        if (library is not null && !WorkPlacementPolicy.MayRunOnWorker(library.WorkPlacement))
        {
            return false;
        }

        if (library?.VideoQualityStrategy == VideoQualityStrategy.AdaptiveVmaf && job.AdaptiveVideoQuality is null)
        {
            return false;
        }

        var targetCodec = LibraryRuleResolution.Resolve(library).TargetVideoCodec;
        return targetCodec is not null
            && EncoderSelector.Select(
                targetCodec,
                EncoderMode.Auto,
                WorkerEncoderCatalogue.Describe(capabilities.VideoEncoders)).Succeeded;
    }

}
