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

    /// <summary>
    /// When set, a video source already encoded at or below this density — measured as bits per
    /// pixel-second, i.e. the file bitrate divided by (width × height), so it is resolution- and
    /// frame-rate-independent — is skipped before any transcode, because re-encoding it to the
    /// target codec is unlikely to save space (e.g. a ~1.6 Mbps 1080p h264 source ≈ 0.8). The
    /// total-file bitrate is used, which overstates the video bitrate, so the check only skips when
    /// a saving is clearly improbable; the size-saving verification gate remains the backstop.
    /// <c>null</c> disables the heuristic (e.g. AV1, efficient enough to shrink low-bitrate sources).
    /// </summary>
    public double? MinSourceBitsPerPixelSecond { get; init; }

    /// <summary>
    /// When set, a file already in <see cref="TargetVideoCodec"/> is still re-encoded if it is at
    /// least this many bytes, to shrink oversized same-codec files (e.g. a large HEVC remux under an
    /// HEVC target). <c>null</c> keeps the conservative default of skipping a same-codec file. The
    /// size-saving verification gate still rejects an output that fails to shrink.
    /// </summary>
    public long? ReencodeSameCodecAboveBytes { get; init; }

    /// <summary>
    /// The profile's default encoder quality target (CRF/CQ) for a re-encode, chosen to be
    /// visually transparent for the profile's codec (e.g. x265 ~24). A per-library
    /// <c>QualityCrf</c> override takes precedence; this is the sane fallback so an encode never
    /// silently uses the encoder's arbitrary built-in default. <c>null</c> for remux-only.
    /// </summary>
    public int? DefaultCrf { get; init; }

    /// <summary>How HDR / Dolby Vision content is handled. Defaults to the safe Exclude.</summary>
    public HdrHandling Hdr { get; init; } = HdrHandling.Exclude;

    /// <summary>
    /// When <c>false</c> (the default), a Dolby Vision source is left untouched regardless of
    /// <see cref="Hdr"/>. Re-encoding or tone-mapping DV without its dynamic-metadata RPU degrades it
    /// to HDR10/SDR, and a Profile 5 source (no HDR10 base layer) comes out green/pink. VMAF cannot
    /// preserve the missing dynamic metadata, so opt in only if losing the Dolby Vision presentation
    /// is acceptable for that library.
    /// </summary>
    public bool OptimiseDolbyVision { get; init; }

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

    /// <summary>
    /// When <c>true</c>, already-lossy audio (e.g. a 320 kbps MP3) is also eligible for
    /// re-encoding to the target codec, but only when its source bitrate is known to exceed
    /// the target enough to genuinely save space. Defaults to <c>false</c>: the conservative
    /// behaviour re-encodes only lossless sources, since re-encoding lossy audio risks
    /// generational quality loss for little gain.
    /// </summary>
    public bool ReencodeLossyAudio { get; init; }

    /// <summary>The format an image is re-encoded to (e.g. "webp"). Defaults to the compatible WebP.</summary>
    public string TargetImageFormat { get; init; } = ImageTarget.DefaultFormat;

    /// <summary>The encoder quality (0–100, higher is better) for an image re-encode.</summary>
    public int ImageQuality { get; init; } = ImageTarget.DefaultQuality;

    /// <summary>
    /// When <c>true</c>, an already-lossy image (e.g. a JPEG) is also eligible for re-encoding to
    /// the target format. Defaults to <c>false</c>: the conservative behaviour re-encodes only
    /// lossless sources (PNG/BMP/TIFF/GIF), since re-encoding a JPEG risks generational loss.
    /// </summary>
    public bool ReencodeLossyImages { get; init; }

    /// <summary>How an image is downscaled on re-encode. Defaults to no resize.</summary>
    public ImageDownscaleMode ImageDownscaleMode { get; init; } = ImageDownscaleMode.None;

    /// <summary>
    /// The downscale magnitude: a maximum long-edge in pixels for
    /// <see cref="Queue.ImageDownscaleMode.MaxLongEdge"/>, or a percentage (1–99) for
    /// <see cref="Queue.ImageDownscaleMode.Percent"/>. Ignored when the mode is
    /// <see cref="Queue.ImageDownscaleMode.None"/>.
    /// </summary>
    public int ImageDownscaleValue { get; init; }
}
