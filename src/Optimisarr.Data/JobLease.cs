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
    public Guid Id { get; set; }

    public int JobId { get; set; }

    public Job? Job { get; set; }

    public int WorkerId { get; set; }

    public Worker? Worker { get; set; }

    public DateTimeOffset AcquiredAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public LeaseState State { get; set; } = LeaseState.Held;

    /// <summary>
    /// The container extension the assignment told the worker to produce, recorded when the
    /// lease is granted. The delivered candidate is named with it, because the replacement takes
    /// its final extension from the candidate's name: a file named after the source but holding
    /// the contract's container would be placed under the wrong extension. Null only for a lease
    /// granted before this existed, and such a lease can no longer deliver.
    /// </summary>
    public string? OutputExtension { get; set; }

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

    public void Apply(WorkerLease lease)
    {
        ExpiresAt = lease.ExpiresUtc;
        State = lease.State;
    }
}
