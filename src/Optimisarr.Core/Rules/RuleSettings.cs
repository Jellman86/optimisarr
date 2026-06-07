using Optimisarr.Core.Domain;

namespace Optimisarr.Core.Rules;

/// <summary>
/// The concrete eligibility settings a <see cref="RuleProfile"/> resolves to.
/// Kept separate from the profile enum so libraries can override individual
/// values later without changing the profile's meaning.
/// </summary>
public sealed record RuleSettings
{
    public required RuleProfile Profile { get; init; }

    /// <summary>
    /// The video codec a re-encode targets (ffprobe codec name, e.g. "hevc").
    /// <c>null</c> means the profile only remuxes/cleans containers, never re-encodes.
    /// </summary>
    public string? TargetVideoCodec { get; init; }

    /// <summary>
    /// ffprobe <c>format_name</c> keywords that count as an already-clean container
    /// for remux profiles (matched case-insensitively as substrings).
    /// </summary>
    public IReadOnlySet<string> AcceptableContainerKeywords { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "matroska" };

    /// <summary>Files smaller than this are not worth optimising.</summary>
    public long MinFileSizeBytes { get; init; }

    /// <summary>When set, files taller than this many pixels are left untouched.</summary>
    public int? MaxHeight { get; init; }

    /// <summary>When true, HDR / Dolby Vision content is excluded to avoid tone-mapping risk.</summary>
    public bool ExcludeHdr { get; init; } = true;

    /// <summary>Relative-path substrings that exclude a file (e.g. "Extras", "Featurettes").</summary>
    public IReadOnlyList<string> ExcludePathSegments { get; init; } = Array.Empty<string>();
}
