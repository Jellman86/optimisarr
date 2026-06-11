using Optimisarr.Core.Domain;

namespace Optimisarr.Data;

public enum MediaFileStatus
{
    Discovered = 0,
    Probed = 1,
    ProbeFailed = 2
}

public sealed class MediaFile
{
    public int Id { get; set; }

    /// <summary>
    /// The library this file was discovered under. Nullable so the schema can be
    /// added to an existing database without violating the foreign key; scans
    /// always set it, and the seeder backfills any legacy rows.
    /// </summary>
    public int? LibraryId { get; set; }

    public Library? Library { get; set; }

    /// <summary>Absolute path to the file on disk.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Path relative to the library root it was discovered under.</summary>
    public string RelativePath { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public DateTimeOffset ModifiedAt { get; set; }

    public DateTimeOffset DiscoveredAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public MediaFileStatus Status { get; set; } = MediaFileStatus.Discovered;

    // --- Probe results (populated once the file has been probed) ---

    /// <summary>What the file actually is — video, audio, or image — detected on probe.</summary>
    public MediaKind MediaKind { get; set; } = MediaKind.Unknown;

    public string? Container { get; set; }

    public double? DurationSeconds { get; set; }

    public string? VideoCodec { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }

    /// <summary>Comma-separated summary of audio codecs, e.g. "eac3, aac".</summary>
    public string? AudioCodecs { get; set; }

    public int? AudioTrackCount { get; set; }

    public int? SubtitleTrackCount { get; set; }

    /// <summary>True when the video stream is HDR10/HLG or carries Dolby Vision side data.</summary>
    public bool IsHdr { get; set; }

    /// <summary>
    /// The Optimisarr fingerprint read from the file's container metadata, if present. A
    /// non-null value means this exact file was produced by Optimisarr, so it is never
    /// optimised again — even on a fresh install or after the queue history is cleared.
    /// </summary>
    public string? OptimisedMarker { get; set; }

    public DateTimeOffset? ProbedAt { get; set; }

    public string? ProbeError { get; set; }
}
