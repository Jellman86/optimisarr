namespace Optimisarr.Core.Queue;

/// <summary>The ffmpeg encoder and output container for a target audio codec.</summary>
public sealed record AudioCodecSpec(string Encoder, string Container);

/// <summary>
/// Audio optimisation targets: the conservative defaults, which source codecs are worth
/// re-encoding, and the encoder/container each supported target codec maps to.
/// </summary>
public static class AudioTarget
{
    /// <summary>The default target codec (ffprobe name) when a library sets no override.</summary>
    public const string DefaultCodec = "opus";

    public const int DefaultBitrateKbps = 128;

    /// <summary>
    /// Audio files below this are not worth optimising. Far smaller than the video default,
    /// since a single lossless track is tens of megabytes, not hundreds.
    /// </summary>
    public const long MinFileSizeBytes = 4L * 1024 * 1024;

    // The supported target codecs and how to produce each. Opus is the most efficient;
    // AAC and MP3 are offered for player compatibility.
    private static readonly IReadOnlyDictionary<string, AudioCodecSpec> Targets =
        new Dictionary<string, AudioCodecSpec>(StringComparer.OrdinalIgnoreCase)
        {
            ["opus"] = new("libopus", "opus"),
            ["aac"] = new("aac", "m4a"),
            ["mp3"] = new("libmp3lame", "mp3")
        };

    /// <summary>The target codecs an operator may choose (ffprobe names).</summary>
    public static IReadOnlyCollection<string> SupportedCodecs => (IReadOnlyCollection<string>)Targets.Keys;

    public static bool IsSupportedTarget(string codec) => Targets.ContainsKey(codec);

    /// <summary>The encoder and container for a target codec; throws if unsupported.</summary>
    public static AudioCodecSpec Resolve(string codec) =>
        Targets.TryGetValue(codec, out var spec)
            ? spec
            : throw new ArgumentOutOfRangeException(nameof(codec), codec, "No known ffmpeg encoder for this target audio codec.");

    // Lossless sources are the safe, worthwhile candidates: re-encoding them saves a lot with
    // no audible loss. Already-lossy audio is left untouched to avoid generational quality loss.
    private static readonly HashSet<string> LosslessCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "flac", "alac", "ape", "wavpack", "tta", "tak", "truehd", "mlp"
    };

    /// <summary>Whether a codec is lossless (PCM variants and the known lossless families).</summary>
    public static bool IsLossless(string codec) =>
        codec.StartsWith("pcm_", StringComparison.OrdinalIgnoreCase) || LosslessCodecs.Contains(codec);
}
