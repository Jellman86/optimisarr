using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Library;
using Optimisarr.Api.Workers;
using Optimisarr.Core.Workers;
using Optimisarr.Data;

namespace Optimisarr.Api.Endpoints;

/// <summary>
/// One job handed to one worker, with the deadline by which it must renew or lose it.
/// The encode policy is resolved here rather than by the worker: the control plane owns the rules.
/// </summary>
internal sealed record AssignmentDto(
    Guid LeaseId,
    int JobId,
    string SourcePath,
    long SourceBytes,
    string VideoEncoder,
    string Vmaf,
    DateTimeOffset ExpiresUtc,
    int RenewWithinSeconds);

internal sealed record LeaseRenewedDto(Guid LeaseId, DateTimeOffset ExpiresUtc);

internal static class WorkerLeaseEndpoints
{
    public static void MapWorkerLeaseEndpoints(this WebApplication app)
    {
        // A worker asking for something to do. 204 when there is nothing it can run, which is the
        // ordinary answer most of the time and not an error.
        app.MapPost("/api/workers/claim", async (
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

            var held = await db.JobLeases
                .CountAsync(lease => lease.WorkerId == worker.Id && lease.State == LeaseState.Held, cancellationToken);
            if (held >= worker.MaxConcurrency)
            {
                return Results.NoContent();
            }

            var capabilities = ToCapabilities(worker);

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
                .Include(job => job.MediaFile)
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

                var requirements = new JobRequirements(
                    VideoEncoder: job.VideoEncoder ?? string.Empty,
                    HardwareDecoder: null,
                    Vmaf: VmafCapability.Cpu,
                    // Scratch for the candidate plus headroom; a worker that cannot hold the output
                    // has no business starting the encode.
                    ScratchBytes: job.MediaFile.SizeBytes + (job.MediaFile.SizeBytes / 2));

                if (!WorkerCapabilityMatcher.Match(capabilities, requirements).Accepted)
                {
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

                return Results.Ok(new AssignmentDto(
                    lease.Id,
                    job.Id,
                    job.MediaFile.Path,
                    job.MediaFile.SizeBytes,
                    job.VideoEncoder ?? string.Empty,
                    VmafCapability.Cpu.ToString(),
                    lease.ExpiresUtc,
                    (int)WorkerLiveness.HeartbeatInterval.TotalSeconds));
            }

            return Results.NoContent();
        })
        .WithName("ClaimWork")
        .Produces<AssignmentDto>()
        .Produces<ApiError>(StatusCodes.Status401Unauthorized);

        app.MapPost("/api/workers/leases/{leaseId:guid}/renew", async (
            Guid leaseId,
            HttpRequest http,
            SettingsStore settings,
            OptimisarrDbContext db,
            CancellationToken cancellationToken) =>
            await MutateLeaseAsync(leaseId, http, settings, db, cancellationToken,
                (lease, workerId, now) => lease.Renew(workerId, now),
                (job, outcome) =>
                {
                    // A renewal changes nothing about the job; it still belongs to the worker.
                },
                lease => Results.Ok(new LeaseRenewedDto(lease.Id, lease.ExpiresUtc))))
        .WithName("RenewLease")
        .Produces<LeaseRenewedDto>()
        .Produces<ApiError>(StatusCodes.Status401Unauthorized);

        app.MapPost("/api/workers/leases/{leaseId:guid}/release", async (
            Guid leaseId,
            HttpRequest http,
            SettingsStore settings,
            OptimisarrDbContext db,
            CancellationToken cancellationToken) =>
            await MutateLeaseAsync(leaseId, http, settings, db, cancellationToken,
                (lease, workerId, now) => lease.Release(workerId, now),
                (job, outcome) =>
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
        Action<Job, LeaseOutcome> applyToJob,
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
            applyToJob(stored.Job, result.Outcome);
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

            // Only a job still sitting in Leased is ours to hand back. One that moved on — because
            // an operator cancelled it, say — must not be dragged back onto the queue.
            if (lease.Job is { Status: JobStatus.Leased } job)
            {
                job.Status = JobStatus.Queued;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static WorkerCapabilities ToCapabilities(Worker worker) => new(
        worker.OperatingSystem,
        worker.Architecture,
        Split(worker.VideoEncoders),
        Split(worker.HardwareDecoders),
        worker.Vmaf,
        worker.FreeScratchBytes,
        worker.MaxConcurrency);

    private static IReadOnlyList<string> Split(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
