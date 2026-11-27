using Optimisarr.Core.Workers;

namespace Optimisarr.Sidecar.Core.Session;

/// <summary>
/// Holds each ordinary job's working space for as long as it runs, so jobs running side by side
/// cannot together need more than the scratch volume has.
///
/// <para>The server checks a job against the free space this machine last reported, and it offers
/// every claim in one check-in against the same figure: on its own that would admit two jobs
/// which each fit but do not fit together. Each job therefore charges the server's own estimate
/// to the shared <see cref="PerceptualJobResourcePool"/> before it fetches anything. The next
/// check-in reports space net of those charges, and a job that no longer fits is handed straight
/// back as a capacity return instead of failing part-way through its download.</para>
///
/// <para>Quality-first searches are passed through untouched: <see cref="JobRunner"/> already
/// reserves their larger peak in the same pool, and charging them here as well would count them
/// twice.</para>
/// </summary>
public sealed class ConcurrentScratchAdmission(
    string scratchRoot,
    Func<string, NativeWorkingResources?>? read = null,
    PerceptualJobResourcePool? pool = null)
{
    public const string OtherJobsHoldSpace =
        "This machine's other running jobs leave too little working space for this one, so it was handed back.";
    public const string TooLittleSpace =
        "This machine no longer has enough free working space for this job, so it was handed back.";
    public const string SpaceUnreadable =
        "This machine's free working space could not be read, so the job was handed back.";
    public const string SizeUnknown =
        "This job's size is unknown, so its working space could not be held and it was handed back.";

    private readonly Func<string, NativeWorkingResources?> _read = read ?? (root => PerceptualJobRuntime.ReadResources(root));
    private readonly PerceptualJobResourcePool _pool = pool ?? PerceptualJobResourcePool.Shared;

    /// <summary>
    /// The job runner for this many jobs at once. One job keeps the path every Windows worker has
    /// run so far; more must hold each job's working space, or jobs that each fit could together
    /// overfill it.
    /// </summary>
    public static Func<StoredPairing, Assignment, CancellationToken, Task<JobOutcome>> ForConcurrency(
        int jobs,
        string scratchRoot,
        Func<StoredPairing, Assignment, CancellationToken, Task<JobOutcome>> run,
        Func<StoredPairing, Guid, CancellationToken, Task> releaseForCapacity,
        Func<string, NativeWorkingResources?>? read = null,
        PerceptualJobResourcePool? pool = null) =>
        jobs > 1 ? new ConcurrentScratchAdmission(scratchRoot, read, pool).Guard(run, releaseForCapacity) : run;

    /// <summary>The same estimate the server admitted the job against.</summary>
    public static long? RequiredBytes(Assignment assignment) =>
        assignment.RequiredScratchBytes ?? WorkerScratchBudget.RequiredBytes(assignment.SourceBytes, assignment.MaxCandidateBytes, null);

    /// <summary>Null, with the reason, when the space cannot be confirmed: a refusal rather than a guess.</summary>
    public IDisposable? TryReserve(Assignment assignment, out string? refusal)
    {
        if (RequiredBytes(assignment) is not { } disk)
        {
            refusal = SizeUnknown;
            return null;
        }
        if (_read(scratchRoot) is not { } resources || (resources.UsesMemory && resources.Memory is null))
        {
            refusal = SpaceUnreadable;
            return null;
        }

        // Disk-backed scratch charges no memory, so it is offered unlimited memory: neither an
        // unreadable memory figure nor memory another job holds says anything about its disk.
        var reservation = resources.UsesMemory
            ? _pool.TryAcquire(resources.Volume, disk, disk, resources.Disk, resources.Memory)
            : _pool.TryAcquire(resources.Volume, disk, 0, resources.Disk, long.MaxValue);
        refusal = reservation is not null ? null
            : OthersHold(resources) ? OtherJobsHoldSpace
            : TooLittleSpace;
        return reservation;
    }

    private bool OthersHold(NativeWorkingResources resources)
    {
        var (disk, memory) = _pool.Available(resources.Volume, resources.Disk, resources.Memory);
        return disk < resources.Disk || (resources.UsesMemory && memory < resources.Memory);
    }

    /// <summary>Wraps a job so it runs only once its working space is held, and frees it after.</summary>
    public Func<StoredPairing, Assignment, CancellationToken, Task<JobOutcome>> Guard(
        Func<StoredPairing, Assignment, CancellationToken, Task<JobOutcome>> run,
        Func<StoredPairing, Guid, CancellationToken, Task> releaseForCapacity) =>
        async (pairing, assignment, token) =>
        {
            if (assignment.PerceptualSearch is not null)
            {
                return await run(pairing, assignment, token);
            }

            using var reservation = TryReserve(assignment, out var refusal);
            if (reservation is null)
            {
                // Not the caller's token: a stopping service must still give the lease back now
                // rather than leave the job stranded until it lapses.
                await releaseForCapacity(pairing, assignment.LeaseId, CancellationToken.None);
                return new JobOutcome(assignment.JobId, Delivered: false, refusal!, CapacityBlocked: true);
            }

            return await run(pairing, assignment, token);
        };
}
