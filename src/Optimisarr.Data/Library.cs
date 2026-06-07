namespace Optimisarr.Data;

/// <summary>The kind of content a library holds. Drives sensible rule defaults.</summary>
public enum MediaType
{
    Film = 0,
    Tv = 1,
    Music = 2,
    Other = 3
}

/// <summary>
/// The optimisation rule profile applied to a library. Profiles map to concrete
/// eligibility and encoder rules in Phase 2; for now they are stored per library.
/// </summary>
public enum RuleProfile
{
    /// <summary>Space saving with a safe, widely compatible HEVC target.</summary>
    ConservativeHevc = 0,

    /// <summary>Maximise device compatibility by targeting H.264.</summary>
    CompatibilityH264 = 1,

    /// <summary>Smallest files using AV1 where hardware/software allows.</summary>
    ExperimentalAv1 = 2,

    /// <summary>Remux/container cleanup only, no re-encode.</summary>
    RemuxCleanup = 3
}

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

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<MediaFile> MediaFiles { get; } = new List<MediaFile>();
}
