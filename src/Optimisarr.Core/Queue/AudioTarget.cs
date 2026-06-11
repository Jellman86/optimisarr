namespace Optimisarr.Core.Queue;

/// <summary>
/// The conservative default target for audio optimisation until per-library audio rules
/// exist. Opus is an efficient, widely supported codec; 128 kbps is transparent for most
/// stereo music while still saving space against lossless or high-bitrate sources.
/// </summary>
public static class AudioTarget
{
    /// <summary>The target codec's ffprobe name, used to detect a file already in target.</summary>
    public const string Codec = "opus";

    /// <summary>The ffmpeg encoder that produces it.</summary>
    public const string Encoder = "libopus";

    public const int BitrateKbps = 128;

    /// <summary>Output container/extension for the re-encoded audio.</summary>
    public const string Container = "opus";

    /// <summary>
    /// Audio files below this are not worth optimising. Far smaller than the video default,
    /// since a single lossless track is tens of megabytes, not hundreds.
    /// </summary>
    public const long MinFileSizeBytes = 4L * 1024 * 1024;

    // Lossless sources are the safe, worthwhile candidates: re-encoding them to Opus saves a
    // lot with no audible loss. Already-lossy audio is left untouched to avoid generational
    // quality loss for little gain.
    private static readonly HashSet<string> LosslessCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "flac", "alac", "ape", "wavpack", "tta", "tak", "truehd", "mlp"
    };

    /// <summary>Whether a codec is lossless (PCM variants and the known lossless families).</summary>
    public static bool IsLossless(string codec) =>
        codec.StartsWith("pcm_", StringComparison.OrdinalIgnoreCase) || LosslessCodecs.Contains(codec);
}
