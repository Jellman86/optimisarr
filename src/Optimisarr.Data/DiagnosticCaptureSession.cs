namespace Optimisarr.Data;

/// <summary>The operator's explicit consent to collect enhanced diagnostic events.</summary>
public sealed class DiagnosticCaptureSession
{
    public Guid Id { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? StoppedAt { get; set; }
    public int? ScopedJobId { get; set; }
    public bool IncludePaths { get; set; }
    public bool PersistAcrossRestart { get; set; }
    public bool Pinned { get; set; }
    public bool HasFailure { get; set; }
    public int RetentionDays { get; set; } = 7;
    public int FailureRetentionDays { get; set; } = 30;
    public long MaximumBytes { get; set; } = 4 * 1024 * 1024;
    public long BytesStored { get; set; }
    public int EventsStored { get; set; }
    public bool EventLimitReached { get; set; }
}

/// <summary>A bounded, append-only transition record with no free-text or secret-bearing fields.</summary>
public sealed class DiagnosticEvent
{
    public long Id { get; set; }
    public string Source { get; set; } = "Server";
    public Guid? InstanceId { get; set; }
    public long? SourceSequence { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public string? DetailsJson { get; set; }
    public Guid SessionId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public int JobId { get; set; }
    public int Attempt { get; set; }
    public Guid? LeaseId { get; set; }
    public int? WorkerId { get; set; }
    public string ReasonCode { get; set; } = string.Empty;
    public string? PreviousStatus { get; set; }
    public string CurrentStatus { get; set; } = string.Empty;
}

/// <summary>A server-clock acknowledgement of a sidecar's final bounded upload.</summary>
public sealed class DiagnosticWorkerReceipt
{
    public Guid SessionId { get; set; }
    public int WorkerId { get; set; }
    public DateTimeOffset LastUploadAt { get; set; }
    public DateTimeOffset? FinalUploadAt { get; set; }
    public long DroppedEvents { get; set; }
}
