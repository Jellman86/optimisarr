namespace Optimisarr.Data;

public enum ReplacementStatus
{
    /// <summary>The original is quarantined and the verified output is in its place.</summary>
    Replaced = 0,

    /// <summary>The original has been restored from quarantine and the output removed.</summary>
    RolledBack = 1,

    /// <summary>
    /// The quarantined original has been deleted because its retention window
    /// expired. The replacement is no longer reversible — there is nothing to restore.
    /// </summary>
    Purged = 2
}

/// <summary>
/// The record of one safe replacement: the original was moved to quarantine and a
/// verified output put in its place. It is the rollback path — every destructive
/// step is recorded here <em>before</em> it happens, so the original can always be
/// restored from <see cref="QuarantinePath"/>.
/// </summary>
public sealed class Replacement
{
    public int Id { get; set; }

    public int JobId { get; set; }

    public Job? Job { get; set; }

    public int MediaFileId { get; set; }

    /// <summary>Where the original lived before replacement (and where rollback restores it).</summary>
    public string OriginalPath { get; set; } = string.Empty;

    /// <summary>Where the original now lives under the trash root.</summary>
    public string QuarantinePath { get; set; } = string.Empty;

    /// <summary>Where the verified output was placed (the original's directory, output extension).</summary>
    public string FinalPath { get; set; } = string.Empty;

    public long OriginalSizeBytes { get; set; }

    public long NewSizeBytes { get; set; }

    /// <summary>
    /// True when a same-filesystem atomic move was not possible and a verified
    /// copy-plus-delete was used instead. Surfaced so the user understands the move.
    /// </summary>
    public bool CrossFilesystem { get; set; }

    public ReplacementStatus Status { get; set; } = ReplacementStatus.Replaced;

    public DateTimeOffset ReplacedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? RolledBackAt { get; set; }

    /// <summary>When the quarantined original was purged after its retention window expired.</summary>
    public DateTimeOffset? PurgedAt { get; set; }
}
