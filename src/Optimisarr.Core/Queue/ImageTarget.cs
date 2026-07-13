namespace Optimisarr.Core.Queue;

/// <summary>The ffmpeg encoder and output file extension for a target image format.</summary>
public sealed record ImageFormatSpec(string Encoder, string Extension);

/// <summary>
/// Image optimisation targets: the conservative defaults, which source formats are worth
/// re-encoding, and the encoder/extension each supported target format maps to. Mirrors
/// <see cref="AudioTarget"/> for the image pipeline. Pure and unit tested.
/// </summary>
public static class ImageTarget
{
    /// <summary>
    /// The default target format. JPEG is the maximally compatible choice — every media server
    /// (including Plex, which does not display WebP) and every client renders it — so a photo
    /// library is safe out of the box. Operators on Jellyfin can move the preset toward WebP for
    /// greater savings.
    /// </summary>
    public const string DefaultFormat = "jpeg";

    /// <summary>
    /// The default encoder quality (0–100; higher is better). 80 is visually transparent for
    /// photographs while still saving substantially over a source JPEG/PNG.
    /// </summary>
    public const int DefaultQuality = 80;

    /// <summary>
    /// Images below this are not worth optimising. Far smaller than the audio/video minimums —
    /// a photo worth re-encoding is hundreds of kilobytes, while an icon or thumbnail is not.
    /// </summary>
    public const long MinFileSizeBytes = 200L * 1024;

    // The supported target formats and how to produce each. They form a compatibility→efficiency
    // axis: JPEG plays everywhere (incl. Plex), while WebP is smaller for Jellyfin/modern clients.
    // AVIF/JXL remain recognised as source codecs below but are not encode targets: the FFmpeg
    // build shipped for production transcoding provides neither libaom-av1 nor libjxl.
    private static readonly IReadOnlyDictionary<string, ImageFormatSpec> Targets =
        new Dictionary<string, ImageFormatSpec>(StringComparer.OrdinalIgnoreCase)
        {
            ["jpeg"] = new("mjpeg", "jpg"),
            ["webp"] = new("libwebp", "webp")
        };

    /// <summary>The target formats an operator may choose.</summary>
    public static IReadOnlyCollection<string> SupportedFormats => (IReadOnlyCollection<string>)Targets.Keys;

    public static bool IsSupportedTarget(string format) => Targets.ContainsKey(format);

    // The formats proven against the exact production FFmpeg and offered per library.
    private static readonly string[] EncodableFormatList = { "jpeg", "webp" };

    /// <summary>The formats an operator may currently choose as a per-library target.</summary>
    public static IReadOnlyList<string> EncodableFormats => EncodableFormatList;

    public static bool IsEncodable(string format) =>
        EncodableFormatList.Contains(format, StringComparer.OrdinalIgnoreCase);

    /// <summary>The encoder and extension for a target format; throws if unsupported.</summary>
    public static ImageFormatSpec Resolve(string format) =>
        Targets.TryGetValue(format, out var spec)
            ? spec
            : throw new ArgumentOutOfRangeException(nameof(format), format, "No known ffmpeg encoder for this target image format.");

    // Lossless source formats are the safe, worthwhile candidates: re-encoding them to a modern
    // format saves a lot with no quality loss. A GIF is palette-based (lossless per frame).
    private static readonly HashSet<string> LosslessSourceCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "png", "bmp", "tiff", "gif"
    };

    /// <summary>Whether a probed image codec is a lossless source worth re-encoding.</summary>
    public static bool IsLossless(string sourceCodec) => LosslessSourceCodecs.Contains(sourceCodec);

    // ffprobe reports the *codec* of a still, which does not match the target format name for the
    // modern formats (an .avif decodes as "av1", a .jxl as "jpegxl"). Map each target to the
    // codec name(s) ffprobe would report for a file already in that format.
    private static readonly IReadOnlyDictionary<string, HashSet<string>> TargetSourceCodecs =
        new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["jpeg"] = new(StringComparer.OrdinalIgnoreCase) { "mjpeg", "jpeg" },
            ["webp"] = new(StringComparer.OrdinalIgnoreCase) { "webp" },
            ["avif"] = new(StringComparer.OrdinalIgnoreCase) { "av1" },
            ["jxl"] = new(StringComparer.OrdinalIgnoreCase) { "jpegxl", "jxl" }
        };

    /// <summary>
    /// Whether a still whose probed codec is <paramref name="sourceCodec"/> is already in
    /// <paramref name="targetFormat"/> (so re-encoding it would not save space).
    /// </summary>
    public static bool IsAlreadyInFormat(string sourceCodec, string targetFormat) =>
        TargetSourceCodecs.TryGetValue(targetFormat, out var codecs) && codecs.Contains(sourceCodec);
}
