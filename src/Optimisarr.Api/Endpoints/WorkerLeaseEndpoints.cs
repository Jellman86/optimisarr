using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Library;
using Optimisarr.Api.Queue;
using Optimisarr.Api.Realtime;
using Optimisarr.Api.Workers;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Scheduling;
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
    /// <summary>What is being encoded, for the worker to show. A job number alone tells an
    /// operator standing at the Mac nothing about which of their files is being worked on.</summary>
    string Title,
    long SourceBytes,
    string VideoEncoder,
    string Vmaf,
    DateTimeOffset ExpiresUtc,
    int RenewWithinSeconds,
    IReadOnlyList<string> Arguments,
    string OutputExtension,
    QualityRequirementDto Quality,
    /// <summary>
    /// The first candidate of a per-title quality search, when this job needs one. The worker
    /// measures it, reports, and is told what to measure next until it is given a quality to encode
    /// at. Null when the quality is already settled and the encode can start immediately.
    /// </summary>
    AdaptiveSearchStepDto? Search = null,
    RemoteVerificationContract? FullVerification = null,
    long? MaxCandidateBytes = null,
    long? MinCandidateBytes = null);

internal sealed record SizeBudgetExceededRequest(long ObservedBytes);
internal sealed record SizeBudgetUndershotRequest(long ObservedBytes);

/// <summary>
/// One candidate of the per-title quality search, on the wire.
///
/// <para>Its measurement is the same <see cref="QualityRequirementDto"/> the assignment's own
/// quality gate uses, and that is the point. The planner's
/// <see cref="Optimisarr.Core.Workers.RemoteQualityContract"/> carries only the five facts the
/// server needs to keep on the lease, and sending that shape instead meant a sidecar holding one
/// type for "how to measure quality" met two different shapes: the macOS decoder required the
/// three missing fields and gave up, leaving `search` silently null, so every searched job was
/// encoded at the library's baseline with no probe ever reported. One shape on the wire is what
/// stops that happening again.</para>
/// </summary>
internal sealed record AdaptiveSearchStepDto(
    int Quality,
    IReadOnlyList<IReadOnlyList<string>> SampleCommands,
    QualityRequirementDto Measurement);

/// <summary>
/// Puts a planned search on the wire in the shape a worker already understands.
///
/// <para>The values come from the contract that was stored on the lease, not from the policy: a
/// report is validated against what the lease holds, so telling the worker a different model or a
/// different threshold than the one it will be judged by would be an invitation to measure the
/// wrong thing. Only the two facts the contract does not carry — how far the commands subsample,
/// and whether they score a clip — are read from the policy the commands were built from.</para>
/// </summary>
internal static class AdaptiveSearchWire
{
    public static AdaptiveSearchStepDto? From(AdaptiveSearchStep? step, VerificationPolicy policy) =>
        step is null
            ? null
            : new AdaptiveSearchStepDto(
                step.Quality,
                step.SampleCommands,
                new QualityRequirementDto(
                    // A search exists only where there is a gate to search against, so this is
                    // always true; it is sent because the worker's measurement reads it.
                    Measure: true,
                    step.Measurement.Model,
                    policy.VmafFrameSubsample,
                    policy.ClipVmafEnabled,
                    step.Measurement.MinimumHarmonicMean,
                    step.Measurement.MinimumMinimum,
                    step.Measurement.Commands,
                    step.Measurement.Sampling));
}

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

/// <summary>
/// What a worker measured for one candidate quality in the per-title search: the bytes its sample
/// encodes produced, and the raw libvmaf logs. It reports no verdict, because whether a candidate
/// met the target needs the library's policy and the pooling rules, and both live here.
/// </summary>
internal sealed record AdaptiveProbeRequest(
    int Quality,
    long EncodedBytes,
    IReadOnlyList<string> Logs,
    /// <summary>
    /// The same bytes split by window, in command order, so the size forecast can say which
    /// scenes grew. Optional for workers that predate it; when sent it must add up to
    /// <c>encodedBytes</c>.
    /// </summary>
    IReadOnlyList<long>? WindowEncodedBytes = null);

/// <summary>
/// Measure this next, or stop searching and encode at this quality. Never both.
/// </summary>
internal sealed record AdaptiveProbeDirectionDto(
    AdaptiveSearchStepDto? NextStep,
    int? SelectedQuality,
    string Reason,
    /// <summary>
    /// The encode to run once the search is over, replacing the arguments the assignment carried.
    ///
    /// <para>Those were built before a quality existed, so they name the library's value. Encoding
    /// with them would run the whole title at the baseline and quietly discard everything the
    /// search just measured.</para>
    /// </summary>
    IReadOnlyList<string>? Arguments = null);

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
internal sealed record RenewRequest(
    string? Stage = null,
    double? EncodedSeconds = null,
    /// <summary>
    /// How busy the worker's machine is, 0-1. Carried here as well as on the check-in because a
    /// renewal happens every few seconds while a job runs, so this is the figure an operator
    /// watching an encode actually sees. Both optional; absent leaves the last reading alone.
    /// </summary>
    double? CpuBusyFraction = null,
    double? GpuBusyFraction = null);

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

            // Who has already given these jobs back, and when. Read in one query rather than per
            // candidate: the shortlist is 25 jobs and this runs on every check-in from every worker.
            var handbacks = await db.JobLeases
                .AsNoTracking()
                .Where(lease => shortlist.Contains(lease.JobId)
                    && lease.State == LeaseState.Released
                    && lease.EndReason == null)
                .Select(lease => new { lease.JobId, lease.WorkerId, lease.EndedAt })
                .ToListAsync(cancellationToken);
            // Distinct workers, not refusals: see HandbackPolicy.MaxRefusingWorkers. One machine
            // refusing the same job three times is one machine's opinion.
            var refusingWorkers = handbacks
                .GroupBy(h => h.JobId)
                .ToDictionary(g => g.Key, g => g.Select(h => h.WorkerId).Distinct().Count());
            var lastHandbackHere = handbacks
                .Where(h => h.WorkerId == worker.Id)
                .GroupBy(h => h.JobId)
                .ToDictionary(g => g.Key, g => g.Max(h => h.EndedAt));

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

                // A job this worker already gave back, or that too many workers have given back.
                // Offering it again straight away is a loop that re-downloads the source each time.
                lastHandbackHere.TryGetValue(job.Id, out var handedBackHere);
                refusingWorkers.TryGetValue(job.Id, out var refusedBy);
                if (!HandbackPolicy.MayOffer(handedBackHere, refusedBy, now))
                {
                    logger.LogInformation(
                        "Job {JobId} not offered to worker {Worker}: {Reason}",
                        job.Id, worker.Name,
                        HandbackPolicy.Explain(handedBackHere, refusedBy, now));
                    continue;
                }

                // The same preparation local dispatch runs, with the encoder chosen from what this
                // worker proved. A refusal is ordinary — an adaptive library, a remux, an encoder
                // the worker lacks — and is logged rather than surfaced, since the worker's answer
                // is simply the next candidate or nothing.
                var plan = await dispatcher.PrepareRemoteWorkAsync(job.Id, capabilities, cancellationToken);
                if (plan.Assignment is not { } assignment)
                {
                    logger.LogInformation(
                        "Job {JobId} not offered to worker {Worker}: {Reason}",
                        job.Id, worker.Name, plan.Reason);
                    continue;
                }

                if (assignment.FullVerification is not null && worker.ProtocolVersion < 2)
                {
                    WorkerProblems.Record(worker, "Sidecar-only verification requires an updated sidecar (protocol 2).", now);
                    await db.SaveChangesAsync(cancellationToken);
                    continue;
                }

                var requirements = new JobRequirements(
                    VideoEncoder: assignment.VideoEncoder,
                    // Null when the audio is copied. When the command names one it has to be
                    // proved: the library form offers Opus and MP3, and a sidecar whose FFmpeg
                    // lacks libopus or libmp3lame can only fail the job and hand it straight back.
                    AudioEncoder: assignment.AudioEncoder,
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
                    // Information, not Debug. A paired worker sitting idle beside a full queue is
                    // the single most confusing thing this feature can do, and the reason was
                    // written only at a level nobody runs in production.
                    logger.LogInformation(
                        "Job {JobId} not offered to worker {Worker}: {Reasons}",
                        job.Id, worker.Name, string.Join(" ", match.Reasons));
                    continue;
                }

                var lease = WorkerLease.Acquire(Guid.NewGuid(), job.Id, worker.Id, now);
                var maxCandidateBytes = SizeBudget.MaxCandidateBytes(
                    job.MediaFile.SizeBytes, assignment.Verification.RequireSizeReduction,
                    job.Type is JobType.Preview or JobType.Calibration,
                    assignment.Verification.MinimumSizeSavingPercent);
                var minCandidateBytes = SizeBudget.MinCandidateBytes(
                    job.MediaFile.SizeBytes, assignment.Verification.RequireSizeReduction,
                    job.Type is JobType.Preview or JobType.Calibration,
                    assignment.Verification.MaximumSizeSavingPercent);

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
                    MaxCandidateBytes = maxCandidateBytes,
                    MinCandidateBytes = minCandidateBytes,
                    HardwareDecoder = assignment.HardwareDecoder,
                    // What the worker was asked to measure, fixed now so the evidence it returns is
                    // judged against this, not against a policy that may have changed since.
                    // The first candidate to measure, and how. Held on the lease so a report can be
                    // checked against the question that was actually asked.
                    AdaptiveAskedQuality = assignment.Search?.Quality,
                    AdaptiveContractJson = assignment.Search is null
                        ? null
                        : JsonSerializer.Serialize(assignment.Search.Measurement, EvidenceJson),
                    VerificationWorkJson = assignment.VerificationWorkJson,
                    VerificationContractJson = assignment.FullVerification is null ? null
                        : JsonSerializer.Serialize(assignment.FullVerification, EvidenceJson),
                    QualityContractJson = assignment.Quality is null
                        ? null
                        : JsonSerializer.Serialize(assignment.Quality, EvidenceJson),
                });

                // The exclusion that matters: off the queue, so this machine will not also run it.
                job.Status = JobStatus.Leased;
                job.ExecutionAttempt += 1;
                job.Progress = 0;
                job.StartedAt = now;
                job.FinishedAt = null;
                job.UpdatedAt = now;
                job.ErrorMessage = null;
                job.FailureCategory = null;
                job.ProcessLog = null;
                job.WorkOutputPath = null;
                job.OutputSizeBytes = null;
                job.VerificationPassed = null;
                job.VerificationReportJson = null;
                job.VerifiedAt = null;
                // The queue shows these for every job. For a remote job they must be what the
                // worker will actually run, not whatever this server last ran for it.
                job.VideoEncoder = assignment.VideoEncoder;
                job.FfmpegArguments = string.Join(' ', assignment.Arguments);
                job.RequestedVideoQuality = assignment.RequestedVideoQuality;
                job.EffectiveVideoQuality = assignment.EffectiveVideoQuality;
                job.VideoQualityMode = assignment.VideoQualityMode;

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
                    // The file name rather than the whole relative path: the worker shows this in
                    // a narrow menu, and the folders above it are the server's business.
                    Path.GetFileName(job.MediaFile.RelativePath),
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
                        assignment.Quality?.Sampling ?? "None"),
                    AdaptiveSearchWire.From(assignment.Search, policy),
                    assignment.FullVerification,
                    maxCandidateBytes,
                    minCandidateBytes));
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
                lease => Results.Ok(new LeaseRenewedDto(lease.Id, lease.ExpiresUtc)),
                renewing => WorkerEndpoints.RecordLoad(
                    renewing, request?.CpuBusyFraction, request?.GpuBusyFraction));

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
        // One exchange of the per-title quality search: the worker reports what it measured, and
        // is told what to do next. At most four of these happen per job.
        app.MapPost("/api/workers/leases/{leaseId:guid}/quality-probe", async (
            Guid leaseId,
            AdaptiveProbeRequest request,
            HttpRequest http,
            SettingsStore settings,
            OptimisarrDbContext db,
            QueueDispatcher dispatcher,
            IHubContext<JobsHub> hub,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var logger = loggerFactory.CreateLogger("Optimisarr.Api.Workers.AdaptiveSearch");
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
                .ThenInclude(media => media!.Library)
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
                // The search dies with the lease. Half a search proves nothing on its own, and the
                // evidence is bound to the encoder that produced it, so the next holder starts over.
                return ApiErrors.Conflict("worker.lease.notHeld", "That lease is no longer held.");
            }

            if (lease.AdaptiveAskedQuality is not { } asked)
            {
                return ApiErrors.Conflict("worker.search.notRequested",
                    "This lease was not asked to search for a quality.");
            }

            // The search's own contract, not the one the finished candidate will be verified
            // against: a sample is a clip judged from its own first frame, while the final
            // measurement cuts windows out of a whole file.
            var contract = lease.AdaptiveContractJson is { } contractJson
                ? JsonSerializer.Deserialize<RemoteQualityContract>(contractJson, EvidenceJson)
                : null;
            if (contract is null)
            {
                return ApiErrors.Conflict("worker.search.notRequested",
                    "This lease asked for no quality search, so no measurement can be reported.");
            }

            // Judged and centred exactly as a local search would be. Both facts come from the
            // job's own work rather than being reconstructed here, because an approximate policy
            // would leave out the catastrophic floor and a different baseline would bracket around
            // a different number.
            if (await dispatcher.GetSearchContextAsync(lease.JobId, worker.ToCapabilities(), cancellationToken)
                is not var (policy, baseline, bypassSizePreflight))
            {
                return ApiErrors.Conflict("worker.search.notRequested",
                    "This job can no longer be read, so its search cannot continue.");
            }

            var prior = lease.AdaptiveProbesJson is { } probesJson
                ? JsonSerializer.Deserialize<List<AdaptiveQualityProbe>>(probesJson, EvidenceJson) ?? []
                : [];

            var progress = AdaptiveSearchCoordinator.Advance(
                baseline, prior, asked, 
                new AdaptiveSearchReport(
                    request.Quality, request.EncodedBytes, request.Logs ?? [], request.WindowEncodedBytes),
                contract,
                policy);
            if (progress is null)
            {
                return ApiErrors.BadRequest("worker.search.reportInvalid",
                    "That report measured a quality this lease did not ask for, or its logs carry no usable score.");
            }

            lease.AdaptiveProbesJson = JsonSerializer.Serialize(progress.Probes, EvidenceJson);

            // Worded as the local search words it, because the two are the same search and a
            // reader should not have to know which machine ran it to read the outcome.
            var probe = progress.Probes.Last();
            logger.LogInformation(
                "Job {JobId}: adaptive quality candidate {Quality} {Outcome} the VMAF target on {Worker} with {EncodedBytes} encoded video bytes ({Scores})",
                lease.JobId,
                probe.Quality,
                probe.MeetsTarget ? "met" : "missed",
                worker.Name,
                probe.EncodedBytes,
                AdaptiveProbeReport.Describe(probe, policy));

            // Measured once per search, on this machine's copy of the source, then kept with the
            // search's contract so later candidates reuse it. Not at all when nothing would be
            // judged by it: no size gate, or an operator already chose to encode anyway.
            var sizeForecast = contract.SizeForecast
                ?? (lease.MaxCandidateBytes is null || bypassSizePreflight
                    ? null
                    : await dispatcher.MeasureSizeForecastBasisAsync(
                        lease.JobId, worker.ToCapabilities(), cancellationToken));
            var sizeReview = SizePreflight.Review(
                sizeForecast,
                progress.Probes,
                probe,
                progress.Decision,
                lease.MaxCandidateBytes,
                bypassSizePreflight);
            if (sizeReview.ShouldHold && lease.Job is { Status: JobStatus.Leased } heldJob)
            {
                var now = DateTimeOffset.UtcNow;
                var release = lease.ToDomain().Release(worker.Id, now);
                if (release.Outcome != LeaseOutcome.Released)
                {
                    return ApiErrors.Conflict("worker.lease.notHeld", "That lease is no longer held.");
                }
                lease.AdaptiveAskedQuality = null;
                lease.Apply(release.Lease, now);
                lease.EndReason = LeaseEndReason.HeldForSizeReview;
                heldJob.Status = JobStatus.AwaitingSizeReview;
                heldJob.AdaptiveVideoQuality = progress.Decision.SelectedQuality;
                heldJob.Progress = 0;
                heldJob.ErrorMessage = sizeReview.Reason;
                heldJob.UpdatedAt = now;
                await db.SaveChangesAsync(cancellationToken);
                logger.LogInformation(
                    "Job {JobId}: worker quality search held the full encode for size review; samples were {Ratio:P1} of the source video over the same scenes, projecting {ProjectedBytes} bytes",
                    heldJob.Id, sizeReview.VideoRatio, sizeReview.ProjectedBytes);
                dispatcher.Wake();
                await hub.Clients.All.SendAsync("jobsChanged", cancellationToken);
                return ApiErrors.Conflict("worker.search.sizeReview", sizeReview.Reason!);
            }

            if (progress.Decision.Complete)
            {
                QueueDispatcher.LogSizeForecast(logger, lease.JobId, sizeReview);
                lease.AdaptiveAskedQuality = null;
                // Recorded on the job, exactly as a local search records it. Without this the
                // server would not know what the worker is encoding at: the value would exist only
                // in the reply the worker acted on, so a recovery retry would start from nothing
                // and the queue would show no chosen quality at all.
                if (lease.Job is { } searched)
                {
                    searched.AdaptiveVideoQuality = progress.Decision.SelectedQuality;
                    searched.UpdatedAt = DateTimeOffset.UtcNow;
                }
                await db.SaveChangesAsync(cancellationToken);

                // Rebuilt now the quality exists, for this worker's encoder. The assignment's
                // arguments were fixed before the search and name the library's value.
                var settled = await dispatcher.PrepareRemoteWorkAsync(
                    lease.JobId, worker.ToCapabilities(), cancellationToken,
                    forceStrictVerification: lease.VerificationContractJson is not null);

                lease.VerificationWorkJson = settled.Assignment?.VerificationWorkJson;
                await db.SaveChangesAsync(cancellationToken);
                return Results.Ok(new AdaptiveProbeDirectionDto(
                    null,
                    progress.Decision.SelectedQuality,
                    progress.Decision.Reason,
                    settled.Assignment?.Arguments));
            }

            // Planned for this worker, so every candidate is measured on the encoder that will do
            // the real encode — which is the whole reason the search travels with the job.
            var next = await dispatcher.PlanAdaptiveStepAsync(
                lease.JobId, progress.Decision.NextQuality!.Value, worker.ToCapabilities(), cancellationToken);
            if (next is null)
            {
                // The search cannot be expressed any further, so it ends where it stands rather
                // than leaving the worker waiting for an instruction that will not come.
                lease.AdaptiveAskedQuality = null;
                if (lease.Job is { } stalled)
                {
                    stalled.AdaptiveVideoQuality = progress.Decision.SelectedQuality;
                    stalled.UpdatedAt = DateTimeOffset.UtcNow;
                }
                await db.SaveChangesAsync(cancellationToken);
                var fallback = await dispatcher.PrepareRemoteWorkAsync(
                    lease.JobId, worker.ToCapabilities(), cancellationToken,
                    forceStrictVerification: lease.VerificationContractJson is not null);
                lease.VerificationWorkJson = fallback.Assignment?.VerificationWorkJson;
                await db.SaveChangesAsync(cancellationToken);
                return Results.Ok(new AdaptiveProbeDirectionDto(
                    null,
                    progress.Decision.SelectedQuality,
                    "No further candidate could be planned; encoding at the selected quality.",
                    fallback.Assignment?.Arguments));
            }

            lease.AdaptiveAskedQuality = next.Quality;
            lease.AdaptiveContractJson = JsonSerializer.Serialize(
                next.Measurement with { SizeForecast = sizeForecast }, EvidenceJson);
            await db.SaveChangesAsync(cancellationToken);

            return Results.Ok(new AdaptiveProbeDirectionDto(
                AdaptiveSearchWire.From(next, policy), null, progress.Decision.Reason));
        })
        .WithName("ReportAdaptiveProbe")
        .Produces<AdaptiveProbeDirectionDto>()
        .Produces<ApiError>(StatusCodes.Status400BadRequest)
        .Produces<ApiError>(StatusCodes.Status401Unauthorized)
        .Produces<ApiError>(StatusCodes.Status403Forbidden)
        .Produces<ApiError>(StatusCodes.Status404NotFound)
        .Produces<ApiError>(StatusCodes.Status409Conflict);

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

        app.MapPost("/api/workers/leases/{leaseId:guid}/size-budget-exceeded", async (
            Guid leaseId,
            SizeBudgetExceededRequest request,
            HttpRequest http,
            SettingsStore settings,
            OptimisarrDbContext db,
            IHubContext<JobsHub> hub,
            CancellationToken cancellationToken) =>
        {
            var result = await MutateLeaseAsync(leaseId, http, settings, db, cancellationToken,
                (lease, workerId, now) => lease.Release(workerId, now),
                (stored, job, _) =>
                {
                    var now = DateTimeOffset.UtcNow;
                    stored.SizeBudgetExceededAtBytes = request.ObservedBytes;
                    job.Status = JobStatus.Failed;
                    job.ErrorMessage = $"Size saving: candidate grew to {request.ObservedBytes:n0} bytes, exceeding this attempt's byte budget.";
                    job.FailureCategory = FailureCategory.SizeSaving;
                    job.FinishedAt = now;
                    job.UpdatedAt = now;
                },
                _ => Results.NoContent(),
                validate: stored => stored.MaxCandidateBytes is not { } maximum
                    || request.ObservedBytes <= maximum
                    || stored.Job?.Status != JobStatus.Leased
                        ? Results.BadRequest(new ApiError("worker.sizeBudget.invalid",
                            "This lease has no exceeded candidate-size budget."))
                        : null,
                afterApply: async (context, _, job) =>
                    await QueueDispatcher.ApplyFailureTrackingAsync(context, job, JobStatus.Failed,
                        ImmediateAutoExclusionReason.SizeSaving),
                alreadyHandled: (stored, workerId) =>
                    stored.WorkerId == workerId
                    && stored.State == LeaseState.Released
                    && stored.SizeBudgetExceededAtBytes == request.ObservedBytes
                    && stored.Job?.Status == JobStatus.Failed
                        ? Results.NoContent() : null);
            if (result is IStatusCodeHttpResult { StatusCode: StatusCodes.Status204NoContent })
                await hub.Clients.All.SendAsync("jobsChanged", cancellationToken);
            return result;
        })
        .WithName("ReportSizeBudgetExceeded")
        .Produces(StatusCodes.Status204NoContent)
        .Produces<ApiError>(StatusCodes.Status400BadRequest)
        .Produces<ApiError>(StatusCodes.Status401Unauthorized)
        .Produces<ApiError>(StatusCodes.Status403Forbidden)
        .Produces<ApiError>(StatusCodes.Status409Conflict);

        app.MapPost("/api/workers/leases/{leaseId:guid}/size-budget-undershot", async (
            Guid leaseId,
            SizeBudgetUndershotRequest request,
            HttpRequest http,
            SettingsStore settings,
            OptimisarrDbContext db,
            IHubContext<JobsHub> hub,
            CancellationToken cancellationToken) =>
        {
            var result = await MutateLeaseAsync(leaseId, http, settings, db, cancellationToken,
                (lease, workerId, now) => lease.Release(workerId, now),
                (stored, job, _) =>
                {
                    var now = DateTimeOffset.UtcNow;
                    stored.SizeBudgetUndershotAtBytes = request.ObservedBytes;
                    job.Status = JobStatus.Failed;
                    job.ErrorMessage = $"Compression ceiling: finished candidate was {request.ObservedBytes:n0} bytes, below the {stored.MinCandidateBytes:n0}-byte floor.";
                    job.FailureCategory = FailureCategory.SizeSaving;
                    job.FinishedAt = now;
                    job.UpdatedAt = now;
                },
                _ => Results.NoContent(),
                validate: stored => stored.MinCandidateBytes is not { } minimum
                    || request.ObservedBytes >= minimum
                    || request.ObservedBytes < 0
                    || stored.Job?.Status != JobStatus.Leased
                        ? Results.BadRequest(new ApiError("worker.sizeBudget.invalid",
                            "This lease has no undershot candidate-size floor."))
                        : null,
                afterApply: async (context, _, job) =>
                    await QueueDispatcher.ApplyFailureTrackingAsync(context, job, JobStatus.Failed,
                        ImmediateAutoExclusionReason.SizeSaving),
                alreadyHandled: (stored, workerId) =>
                    stored.WorkerId == workerId
                    && stored.State == LeaseState.Released
                    && stored.SizeBudgetUndershotAtBytes == request.ObservedBytes
                    && stored.Job?.Status == JobStatus.Failed
                        ? Results.NoContent() : null);
            if (result is IStatusCodeHttpResult { StatusCode: StatusCodes.Status204NoContent })
                await hub.Clients.All.SendAsync("jobsChanged", cancellationToken);
            return result;
        })
        .WithName("ReportSizeBudgetUndershot")
        .Produces(StatusCodes.Status204NoContent)
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
                    // Requeue unfinished work, but preserve cancellation if it won while the
                    // worker was stopping its child process. Release still frees lease capacity.
                    if (job.Status == JobStatus.Leased)
                    {
                        job.Status = JobStatus.Queued;
                    }
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
        Func<WorkerLease, IResult> success,
        // Runs only once the lease operation has succeeded, so a refused or lapsed renewal records
        // nothing about the machine that sent it.
        Action<Worker>? applyToWorker = null,
        Func<JobLease, IResult?>? validate = null,
        Func<OptimisarrDbContext, JobLease, Job, Task>? afterApply = null,
        Func<JobLease, int, IResult?>? alreadyHandled = null)
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

        if (alreadyHandled?.Invoke(stored, worker.Id) is { } repeated)
        {
            return repeated;
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

        if (validate?.Invoke(stored) is { } invalid)
        {
            return invalid;
        }

        stored.Apply(result.Lease, now);
        if (stored.Job is not null)
        {
            applyToJob(stored, stored.Job, result.Outcome);
            if (afterApply is not null) await afterApply(db, stored, stored.Job);
        }
        // The authenticated worker, not `stored.Worker`: that navigation is not included by the
        // query above, so reaching through it would have compiled, run, and silently recorded
        // nothing at all.
        applyToWorker?.Invoke(worker);

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
            lease.EndedAt ??= now;

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

        // A job whose per-title quality is still unchosen used to be refused here, because the
        // search could only run on the server. It now travels with the job, so this is no longer a
        // reason to keep the work local. Whether a search can actually be expressed as commands
        // needs the source's picture and duration, which this cheap pre-filter does not load —
        // PrepareRemoteWorkAsync decides that, and refuses the job there if it cannot.

        var targetCodec = LibraryRuleResolution.Resolve(library).TargetVideoCodec;
        return targetCodec is not null
            && EncoderSelector.Select(
                targetCodec,
                EncoderMode.Auto,
                WorkerEncoderCatalogue.Describe(capabilities.VideoEncoders)).Succeeded;
    }

}
