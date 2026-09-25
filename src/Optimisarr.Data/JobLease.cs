using Optimisarr.Core.Workers;

namespace Optimisarr.Data;

/// <summary>
/// A remote worker's exclusive claim on one job.
///
/// The persisted half of <see cref="WorkerLease"/>. Expiry is stored so it survives a restart, but
/// it is always re-derived through the domain type when read: a lease past its expiry is expired
/// the moment it is looked at, whether or not anything has swept it.
///
/// The claim is enforced by the job's own status rather than by this row. A leased job leaves
/// <see cref="JobStatus.Queued"/>, so the local dispatcher stops seeing it — the exclusion cannot
/// be forgotten by a query that neglects to join here.
/// </summary>
public sealed class JobLease
{
    /// <summary>Frozen server-side work and measurement request for strict sidecar verification.</summary>
    public string? VerificationWorkJson { get; set; }
    public string? VerificationContractJson { get; set; }
    public string? VerificationEvidenceJson { get; set; }

    public Guid Id { get; set; }

    public int JobId { get; set; }

    public Job? Job { get; set; }

    public int WorkerId { get; set; }

    public Worker? Worker { get; set; }

    public DateTimeOffset AcquiredAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public LeaseState State { get; set; } = LeaseState.Held;

    /// <summary>
    /// When the lease stopped being held, whatever the reason. Needed because a worker that hands
    /// a job back must not be offered the same job again immediately: without a time to compare
    /// against, the only options are to re-offer it at once, which loops, or never again, which
    /// punishes a worker that was merely asleep.
    /// </summary>
    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>
    /// The container extension the assignment told the worker to produce, recorded when the
    /// lease is granted. The delivered candidate is named with it, because the replacement takes
    /// its final extension from the candidate's name: a file named after the source but holding
    /// the contract's container would be placed under the wrong extension. Null only for a lease
    /// granted before this existed, and such a lease can no longer deliver.
    /// </summary>
    public string? OutputExtension { get; set; }

    /// <summary>
    /// Frozen maximum candidate size for this attempt. Null means the size-saving gate is off;
    /// older workers may ignore it, but a reporting worker cannot invent a different limit.
    /// </summary>
    public long? MaxCandidateBytes { get; set; }

    /// <summary>Frozen minimum final size for an optional maximum-compression gate.</summary>
    public long? MinCandidateBytes { get; set; }

    /// <summary>The partial candidate size observed when the frozen budget stopped this lease.</summary>
    public long? SizeBudgetExceededAtBytes { get; set; }

    /// <summary>The completed candidate size when it undershot the compression floor.</summary>
    public long? SizeBudgetUndershotAtBytes { get; set; }

    /// <summary>Where the worker says it is, from its latest renewal. Null until it reports.</summary>
    public RemoteStage? Stage { get; set; }

    /// <summary>
    /// The measurement the worker was asked to make, serialised <see cref="RemoteQualityContract"/>,
    /// fixed at claim so returned evidence is judged against exactly what was asked. Null when the
    /// job's policy had no quality gate.
    /// </summary>
    public string? QualityContractJson { get; set; }

    /// <summary>The pooled scores the server parsed from the worker's libvmaf logs. Null until reported.</summary>
    public string? QualityScoresJson { get; set; }

    /// <summary>
    /// Candidates this worker has measured so far in the per-title quality search, as a serialised
    /// list of <see cref="Optimisarr.Core.Queue.AdaptiveQualityProbe"/>. Null for a job whose
    /// quality was already settled before it was offered.
    ///
    /// <para>On the lease rather than the job, because the search is bound to the machine running
    /// it: a quality proven by measuring one encoder means nothing on another, so half a search
    /// from a worker that vanished must not be inherited by whoever picks the job up next. A lapsed
    /// lease takes its evidence with it, and the next attempt starts again.</para>
    /// </summary>
    public string? AdaptiveProbesJson { get; set; }

    /// <summary>
    /// How to measure the candidate currently being searched, serialised
    /// <see cref="RemoteQualityContract"/>.
    ///
    /// <para>Separate from <see cref="QualityContractJson"/>, which is the contract the finished
    /// candidate will be verified against. They describe different measurements — a sample is a
    /// clip judged from its own first frame, the final candidate is a whole file with windows cut
    /// out of it — so reusing one field for both would leave the search's last sample standing in
    /// for the verification contract once the search ended.</para>
    /// </summary>
    public string? AdaptiveContractJson { get; set; }

    /// <summary>
    /// The candidate quality this worker was last asked to measure. Held so a report can be checked
    /// against the question: a worker answers what it was asked or it answers nothing, since the
    /// search brackets and every later candidate derives from this one.
    /// </summary>
    public int? AdaptiveAskedQuality { get; set; }

    /// <summary>
    /// Set when the control plane, not the worker, ended the lease. Such a release is not a
    /// handback, so it neither pauses the job for this worker nor counts towards barring it.
    /// </summary>
    public LeaseEndReason? EndReason { get; set; }

    /// <summary>The source hash the worker says it measured against.</summary>
    public string? QualitySourceSha256 { get; set; }

    /// <summary>The candidate hash the worker says it measured; must match what it then delivers.</summary>
    public string? QualityCandidateSha256 { get; set; }

    /// <summary>The hash of the candidate actually received, computed here as it arrived.</summary>
    public string? DeliveredSha256 { get; set; }

    /// <summary>
    /// The hardware decoder the assignment told the worker to use, or null for software decode.
    /// Recorded so a delivered candidate that fails with the signature of decoder corruption can be
    /// tried again in software rather than failed outright.
    /// </summary>
    public string? HardwareDecoder { get; set; }

    /// <summary>
    /// Seconds of output the worker's ffmpeg had produced at its latest renewal. The server turns
    /// this into a fraction against the source duration, because the worker never learns it.
    /// </summary>
    public double? EncodedSeconds { get; set; }

    /// <summary>Rebuilds the domain lease so every decision runs through one state machine.</summary>
    public WorkerLease ToDomain() =>
        new(Id, JobId, WorkerId, AcquiredAt, ExpiresAt, State);

    public void Apply(WorkerLease lease, DateTimeOffset now)
    {
        ExpiresAt = lease.ExpiresUtc;
        State = lease.State;
        // Stamped on the way out of Held, and only once: a lease that is released and later
        // reclaimed by the expiry sweep should keep the time it actually stopped being worked.
        if (lease.State != LeaseState.Held && EndedAt is null)
        {
            EndedAt = now;
        }
    }
}
