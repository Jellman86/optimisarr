using Optimisarr.Core.Deduplication;

namespace Optimisarr.Data;

/// <summary>Independent of encode replacement history and its retention/purge policy.</summary>
public sealed class DedupeAction
{
    public Guid Id { get; set; }
    public int LibraryId { get; set; }
    public string Contract { get; set; } = DedupePreparationPolicy.Contract;
    public string Retention { get; set; } = DedupePreparationPolicy.Retention;
    public string ReviewKey { get; set; } = string.Empty;
    public string ScopeKey { get; set; } = string.Empty;
    public string PlanHash { get; set; } = string.Empty;
    public DedupeActionState State { get; set; } = DedupeActionState.Prepared;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? AttentionReason { get; set; }
}

/// <summary>IDs are historical observations, deliberately not cascading inventory/library foreign keys.</summary>
public sealed class DedupeActionFile
{
    public Guid ActionId { get; set; }
    public int MediaFileId { get; set; }
    public int LibraryId { get; set; }
    public string Root { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string? QuarantinePath { get; set; }
    public string Identity { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public long ModifiedUtcTicks { get; set; }
    public bool IsKeeper { get; set; }
}

/// <summary>Unresolved reservations survive process exit and cannot be acquired by another action.</summary>
public sealed class DedupeFileClaim
{
    public string Key { get; set; } = string.Empty;
    public Guid ActionId { get; set; }
}
