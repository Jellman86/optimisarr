namespace Optimisarr.Data;

public enum ExactDuplicateScanState { Queued, Running, Completed, Cancelled, Failed, Interrupted }

public sealed class ExactDuplicateScan
{
    public long Id { get; set; }
    public int LibraryId { get; set; }
    public string Root { get; set; } = string.Empty;
    public ExactDuplicateScanState State { get; set; }
    public int? ActiveSlot { get; set; }
    public string ScopeSignature { get; set; } = string.Empty;
    public bool ForceRefresh { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public int Checked { get; set; }
    public int Skipped { get; set; }
    public int Total { get; set; }
    public int Reused { get; set; }
    public long BytesRead { get; set; }
    public string? Error { get; set; }
}

/// <summary>Only a completed, stable content read creates a reusable fingerprint.</summary>
public sealed class ExactFileFingerprint
{
    public int MediaFileId { get; set; }
    public string Root { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public long ModifiedUtcTicks { get; set; }
    public string? Identity { get; set; }
    public string Contract { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public DateTimeOffset CheckedAt { get; set; }
}

/// <summary>A completed unit of a scan; partial SHA-256 state is never persisted.</summary>
public sealed class ExactDuplicateObservation
{
    public long ScanId { get; set; }
    public int MediaFileId { get; set; }
    public string RelativePath { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public long ModifiedUtcTicks { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public int? HardLinkCount { get; set; }
    public DateTimeOffset CheckedAt { get; set; }
    public bool Reused { get; set; }
}
