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
}
