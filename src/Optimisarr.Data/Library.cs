using Optimisarr.Core.Domain;

namespace Optimisarr.Data;

/// <summary>
/// A configured media library root. Each library has its own media type and rule
/// profile so different content (TV, film, music) can be optimised differently.
/// </summary>
public sealed class Library
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Absolute path to the library root on disk.</summary>
    public string Path { get; set; } = string.Empty;

    public MediaType MediaType { get; set; } = MediaType.Other;

    public RuleProfile RuleProfile { get; set; } = RuleProfile.ConservativeHevc;

    /// <summary>When false, the library is skipped by scans.</summary>
    public bool Enabled { get; set; } = true;

    // --- Per-library rule overrides. Null means "use the profile default"; the
    // effective settings are resolved by Optimisarr.Core.Rules.RuleResolver. ---

    /// <summary>Queue priority; higher runs sooner. Defaults to 0.</summary>
    public int Priority { get; set; }

    public long? MinFileSizeBytes { get; set; }

    /// <summary>Files taller than this (pixels) are skipped.</summary>
    public int? MaxHeight { get; set; }

    /// <summary>Overrides the profile's target video codec (ffprobe name, e.g. "hevc").</summary>
    public string? TargetVideoCodec { get; set; }

    /// <summary>Overrides the profile's target container (e.g. "mkv").</summary>
    public string? TargetContainer { get; set; }

    /// <summary>Overrides the profile's HDR / Dolby Vision handling.</summary>
    public HdrHandling? HdrHandling { get; set; }

    /// <summary>Newline-separated relative-path substrings to exclude (e.g. "Extras").</summary>
    public string? ExcludePaths { get; set; }

    /// <summary>Encoder quality target (CRF/CQ). Null uses the encoder default.</summary>
    public int? QualityCrf { get; set; }

    /// <summary>Encoder speed/quality preset (e.g. "medium", "slow"). Null uses the encoder default.</summary>
    public string? EncoderPreset { get; set; }

    /// <summary>Overrides the codec lossless audio is re-encoded to (e.g. "opus", "aac", "mp3"). Null uses the default.</summary>
    public string? AudioTargetCodec { get; set; }

    /// <summary>Overrides the audio re-encode bitrate in kbps. Null uses the default.</summary>
    public int? AudioBitrateKbps { get; set; }

    /// <summary>
    /// Per-library overrides for the perceptual-quality (VMAF) gate, letting an
    /// "archive" library demand near-lossless quality while a "space-saver" accepts
    /// more. Null uses the global threshold; only applied when the gate is enabled.
    /// </summary>
    public double? MinVmafHarmonicMean { get; set; }

    public double? MinVmafMin { get; set; }

    /// <summary>
    /// When true, a completed output is moved into <see cref="TargetFolder"/> (mirroring
    /// the library's relative layout) and the job is marked Completed. The original is
    /// never touched — handy for testing without consuming the source.
    /// </summary>
    public bool MoveOnComplete { get; set; }

    /// <summary>Destination root for completed outputs when <see cref="MoveOnComplete"/> is on.</summary>
    public string? TargetFolder { get; set; }

    // --- Automatic scan-and-enqueue. When enabled, a background worker scans the
    // library and enqueues its eligible candidates once per occurrence of the daily
    // window below. Jobs still only *run* inside the global processing window. ---

    /// <summary>When true, the library is scanned and enqueued automatically on a schedule.</summary>
    public bool AutoEnqueueEnabled { get; set; }

    /// <summary>Local time the daily auto-enqueue window opens. Start == End means all day.</summary>
    public TimeOnly AutoEnqueueWindowStart { get; set; }

    /// <summary>Local time the daily auto-enqueue window closes.</summary>
    public TimeOnly AutoEnqueueWindowEnd { get; set; }

    /// <summary>When the library was last auto-enqueued; null means never.</summary>
    public DateTimeOffset? LastAutoEnqueueAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<MediaFile> MediaFiles { get; } = new List<MediaFile>();
}
