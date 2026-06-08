namespace Optimisarr.Data;

/// <summary>
/// The lifecycle of a transcode job. Terminal states are Completed, Failed, and
/// Cancelled. ReadyToReplace is the end of Phase 3: a verified output exists in
/// <c>/work</c> but the original is never touched until safe replacement (Phase 5).
/// </summary>
public enum JobStatus
{
    Queued = 0,
    Probing = 1,
    Transcoding = 2,
    Verifying = 3,
    ReadyToReplace = 4,
    Completed = 5,
    Failed = 6,
    Cancelled = 7
}

/// <summary>
/// One unit of optimisation work for a single media file. A job never deletes or
/// overwrites the original; it only produces an output under <c>/work</c>.
/// </summary>
public sealed class Job
{
    public int Id { get; set; }

    public int MediaFileId { get; set; }

    public MediaFile? MediaFile { get; set; }

    /// <summary>Denormalised from the media file's library so scheduling needs no join.</summary>
    public int? LibraryId { get; set; }

    public JobStatus Status { get; set; } = JobStatus.Queued;

    /// <summary>Queue priority, snapshotted from the library when the job is enqueued.</summary>
    public int Priority { get; set; }

    /// <summary>How many times this job has been started; incremented on crash recovery.</summary>
    public int Attempt { get; set; }

    /// <summary>Path to the produced output under <c>/work</c>, once transcoding begins.</summary>
    public string? WorkOutputPath { get; set; }

    /// <summary>The exact ffmpeg argument list used, joined for display on the job detail page.</summary>
    public string? FfmpegArguments { get; set; }

    /// <summary>Transcode progress in the range 0..1, parsed from ffmpeg.</summary>
    public double Progress { get; set; }

    public string? ErrorMessage { get; set; }

    // --- Verification (Phase 4: populated once the output has been verified) ---

    /// <summary>Size of the produced output in bytes, recorded at verification time.</summary>
    public long? OutputSizeBytes { get; set; }

    /// <summary>Whether the output passed every verification gate. Null until verified.</summary>
    public bool? VerificationPassed { get; set; }

    /// <summary>The full verification report (per-check outcomes) serialised as JSON for display.</summary>
    public string? VerificationReportJson { get; set; }

    public DateTimeOffset? VerifiedAt { get; set; }

    public DateTimeOffset EnqueuedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
