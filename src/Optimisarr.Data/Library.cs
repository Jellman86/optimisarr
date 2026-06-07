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

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<MediaFile> MediaFiles { get; } = new List<MediaFile>();
}
