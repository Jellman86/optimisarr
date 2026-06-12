using Optimisarr.Core.Domain;
using Optimisarr.Core.Queue;

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
    /// The container to remux/mux into (e.g. "mkv"). A file whose container already
    /// matches is considered clean for remux-only profiles.
    /// </summary>
    public string TargetContainer { get; init; } = "mkv";

    /// <summary>Files smaller than this are not worth optimising.</summary>
    public long MinFileSizeBytes { get; init; }

    /// <summary>When set, files taller than this many pixels are left untouched.</summary>
    public int? MaxHeight { get; init; }

    /// <summary>How HDR / Dolby Vision content is handled. Defaults to the safe Exclude.</summary>
    public HdrHandling Hdr { get; init; } = HdrHandling.Exclude;

    /// <summary>Relative-path substrings that exclude a file (e.g. "Extras", "Featurettes").</summary>
    public IReadOnlyList<string> ExcludePathSegments { get; init; } = Array.Empty<string>();

    /// <summary>The codec a lossless audio file is re-encoded to (ffprobe name, e.g. "opus").</summary>
    public string TargetAudioCodec { get; init; } = AudioTarget.DefaultCodec;

    /// <summary>The bitrate (kbps) for the audio re-encode.</summary>
    public int AudioBitrateKbps { get; init; } = AudioTarget.DefaultBitrateKbps;

    /// <summary>
    /// The codec a *video* job re-encodes its audio tracks to (ffprobe name, e.g. "aac").
    /// <c>null</c> (the default) copies the audio untouched, so nothing changes unless the
    /// operator opts in. Separate from <see cref="TargetAudioCodec"/>, which governs
    /// audio-only files.
    /// </summary>
    public string? VideoAudioCodec { get; init; }

    /// <summary>The bitrate (kbps) for a video's audio re-encode, used only when <see cref="VideoAudioCodec"/> is set.</summary>
    public int VideoAudioBitrateKbps { get; init; } = AudioTarget.DefaultVideoAudioBitrateKbps;

    /// <summary>
    /// When <c>true</c>, multichannel audio is downmixed to 2.0 stereo on re-encode. Applies to
    /// audio-only jobs and to the audio tracks of a video transcode (only where the audio is
    /// actually re-encoded — a copied track keeps its layout). Defaults to <c>false</c> so
    /// surround is preserved unless the operator opts in.
    /// </summary>
    public bool DownmixToStereo { get; init; }
}
