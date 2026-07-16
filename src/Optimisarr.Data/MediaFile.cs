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

    public string? VideoProfile { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }

    /// <summary>Number of frames in the picture/video stream. >1 marks an animated image.</summary>
    public int? FrameCount { get; set; }

    /// <summary>Whether ffprobe found material divergence between nominal and average frame rate.</summary>
    public bool? IsVariableFrameRate { get; set; }

    /// <summary>Decoded pixel format used to prevent alpha or high-bit-depth image loss.</summary>
    public string? PixelFormat { get; set; }

    public int? BitsPerRawSample { get; set; }

    /// <summary>Embedded album-art streams reported with the attached-picture disposition.</summary>
    public int AttachedPictureCount { get; set; }

    /// <summary>Comma-separated summary of audio codecs, e.g. "eac3, aac".</summary>
    public string? AudioCodecs { get; set; }

    /// <summary>
    /// Comma-separated audio track languages in stream order, e.g. "eng, jpn, und" ("und"
    /// standing in for an untagged track). Null when the file was probed before languages
    /// were captured; language rules treat that as unknown and change nothing.
    /// </summary>
    public string? AudioLanguages { get; set; }

    public int? AudioTrackCount { get; set; }

    /// <summary>Largest channel count among the source audio streams.</summary>
    public int MaxAudioChannels { get; set; }

    /// <summary>
    /// The source audio bitrate in kbps (the highest audio stream, or the container bitrate for
    /// an audio-only file). Null when ffprobe reported none. Drives the any-source audio
    /// eligibility check, which only re-encodes lossy audio when a saving can be proven.
    /// </summary>
    public int? AudioBitrateKbps { get; set; }

    public int? SubtitleTrackCount { get; set; }

    /// <summary>
    /// Comma-separated subtitle track languages in stream order, e.g. "eng, und, fra"
    /// ("und" standing in for an untagged track). Null when the file was probed before
    /// subtitle languages were captured; language rules treat that as unknown and change
    /// nothing.
    /// </summary>
    public string? SubtitleLanguages { get; set; }

    /// <summary>True when the video stream is HDR10/HLG or carries Dolby Vision side data.</summary>
    public bool IsHdr { get; set; }

    /// <summary>
    /// True when the video stream carries Dolby Vision (DOVI side data or a dvhe/dvh1/dav1 codec
    /// tag). Tracked separately from <see cref="IsHdr"/> so DV can be left untouched by default — its
    /// RPU cannot survive a re-encode, and a Profile 5 source comes out green/pink.
    /// </summary>
    public bool IsDolbyVision { get; set; }

    /// <summary>
    /// The Optimisarr fingerprint read from the file's container metadata, if present. A
    /// non-null value means this exact file was produced by Optimisarr, so it is never
    /// optimised again — even on a fresh install or after the queue history is cleared.
    /// </summary>
    public string? OptimisedMarker { get; set; }

    public DateTimeOffset? ProbedAt { get; set; }

    public string? ProbeError { get; set; }

    /// <summary>
    /// How many times a job for this file's current version has terminally failed. Drives the
    /// automatic exclusion of a file that keeps failing; a successful encode resets it to zero,
    /// and removing the resulting exclusion resets it so the file gets a fresh start.
    /// </summary>
    public int FailureCount { get; set; }
}
