using Optimisarr.Core.Queue;

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
/// Why a job exists. A <see cref="Normal"/> job can replace its original after verification;
/// a <see cref="Preview"/> job is a throwaway run of a library's settings on one file; a
/// <see cref="Calibration"/> job is one blinded candidate clip. Disposable jobs never replace,
/// move, or survive a restart.
/// </summary>
public enum JobType
{
    Normal = 0,
    Preview = 1,
    Calibration = 2
}

/// <summary>
/// One unit of optimisation work for a single media file. A job never deletes or
/// overwrites the original; it only produces an output under <c>/work</c>.
/// </summary>
public sealed class Job
{
    public int Id { get; set; }

    public int MediaFileId { get; set; }

    /// <summary>Normal replaceable work or a disposable preview/calibration candidate.</summary>
    public JobType Type { get; set; } = JobType.Normal;

    public MediaFile? MediaFile { get; set; }

    /// <summary>Denormalised from the media file's library so scheduling needs no join.</summary>
    public int? LibraryId { get; set; }

    public JobStatus Status { get; set; } = JobStatus.Queued;

    /// <summary>Queue priority, snapshotted from the library when the job is enqueued.</summary>
    public int Priority { get; set; }

    /// <summary>How many times this job has been started; incremented on crash recovery.</summary>
    public int Attempt { get; set; }

    /// <summary>
    /// Why this job was enqueued — the eligibility reason computed at enqueue time
    /// (e.g. "h264 → hevc", "Remove 2 audio track(s) (fra, deu) not in the kept
    /// languages"), shown in the queue so a row explains itself. Null for jobs that
    /// predate the column.
    /// </summary>
    public string? EnqueueReason { get; set; }

    /// <summary>Path to the produced output under <c>/work</c>, once transcoding begins.</summary>
    public string? WorkOutputPath { get; set; }

    /// <summary>The exact ffmpeg argument list used, joined for display on the job detail page.</summary>
    public string? FfmpegArguments { get; set; }

    /// <summary>
    /// The resolved video encoder for this job (e.g. <c>hevc_nvenc</c>, <c>libx265</c>), so the UI
    /// can show whether it ran on the GPU or CPU. Null for a remux/audio/image job.
    /// </summary>
    public string? VideoEncoder { get; set; }

    /// <summary>Library/profile quality number requested before encoder-specific calibration.</summary>
    public int? RequestedVideoQuality { get; set; }

    /// <summary>Audio bitrate requested by a disposable blind-calibration candidate.</summary>
    public int? RequestedAudioBitrateKbps { get; set; }

    /// <summary>Image quality requested by a disposable blind-calibration candidate.</summary>
    public int? RequestedImageQuality { get; set; }

    /// <summary>Encoder-specific quality value passed to FFmpeg for the current attempt.</summary>
    public int? EffectiveVideoQuality { get; set; }

    /// <summary>Encoder quality mode shown to the operator (CRF, ICQ, CQ, or QP).</summary>
    public string? VideoQualityMode { get; set; }

    /// <summary>Number of automatic or operator-requested higher-quality retries.</summary>
    public int QualityRetryCount { get; set; }

    /// <summary>The disposable blind-calibration session that owns this candidate job.</summary>
    public Guid? CalibrationSessionId { get; set; }

    /// <summary>The source offset used for this short calibration candidate.</summary>
    public int? CalibrationClipStartSeconds { get; set; }

    /// <summary>The duration of this short calibration candidate.</summary>
    public int? CalibrationClipSeconds { get; set; }

    /// <summary>
    /// Hidden decode pre-roll at the start of the stream-copy original reference. The blind
    /// player begins here so original and candidate show the same requested source frame.
    /// </summary>
    public double? CalibrationReferenceStartSeconds { get; set; }

    /// <summary>Transcode progress in the range 0..1, parsed from ffmpeg.</summary>
    public double Progress { get; set; }

    public string? ErrorMessage { get; set; }

    /// <summary>
    /// The classified reason a job failed, derived from <see cref="ErrorMessage"/> at the moment it
    /// fails and stored so the diagnostics summary can group in the database and the class survives an
    /// edited message. Null until (and unless) the job fails.
    /// </summary>
    public FailureCategory? FailureCategory { get; set; }

    /// <summary>
    /// The captured ffmpeg log for the run that failed — its non-progress stderr (stream mapping,
    /// warnings, and the error that ended it), with the thousands of progress frames filtered out and
    /// the middle elided if very long. Populated only on failure, so the full "why" is available from
    /// the API (<c>GET /api/jobs/{id}/log</c>) without reading container logs. Null otherwise.
    /// </summary>
    public string? ProcessLog { get; set; }

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
