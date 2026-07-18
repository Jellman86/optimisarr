namespace Optimisarr.Data;

/// <summary>Why a file is on the exclusion (skip) list.</summary>
public enum ExclusionSource
{
    /// <summary>The operator explicitly excluded the file (e.g. from the Queue or Libraries page).</summary>
    Manual = 0,

    /// <summary>Excluded automatically after an unrecoverable or repeated failure.</summary>
    RepeatedFailures = 1
}

/// <summary>
/// A file the operator never wants optimised. Keyed by absolute <see cref="Path"/> so the
/// exclusion is durable: it survives clearing the queue, re-scanning, and even removing and
/// re-adding the library — unlike the soft "previously failed" skip, which hangs off a Failed
/// job row and disappears the moment that history is cleared.
/// </summary>
public sealed class Exclusion
{
    public int Id { get; set; }

    /// <summary>Absolute path of the excluded file. Unique — one exclusion per file.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>The library the file belonged to when excluded, for grouping in its Excluded tab.</summary>
    public int? LibraryId { get; set; }

    /// <summary>The file's library-relative path when excluded, for a friendly display.</summary>
    public string? RelativePath { get; set; }

    /// <summary>An optional human note, e.g. "keeps failing verification".</summary>
    public string? Reason { get; set; }

    public ExclusionSource Source { get; set; } = ExclusionSource.Manual;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
