namespace Optimisarr.Sidecar.Core.Capabilities;

/// <summary>
/// Reads <c>ffmpeg -encoders</c>.
///
/// Listing is only the cheap first pass. FFmpeg lists what it was compiled with, not what this
/// machine can open: a build carrying NVENC says so on a laptop with no NVIDIA card at all, and a
/// driver too old for the build fails when the encoder is first opened rather than when it is
/// listed. Every name found here is proved with a real encode before it is advertised — the macOS
/// sidecar learned that the hard way, with a bundled libx265 that segfaulted on its first frame
/// while being offered to the server as a capability.
/// </summary>
public static class EncoderListParser
{
    /// <summary>
    /// Encoders worth reporting: the ones Optimisarr's own encoder selection can ask a worker for.
    /// Advertising anything else only invites work this sidecar has no better claim to than the
    /// server itself.
    /// </summary>
    public static readonly IReadOnlySet<string> Interesting = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Software.
        "libx264", "libx265", "libsvtav1",
        // NVIDIA.
        "h264_nvenc", "hevc_nvenc", "av1_nvenc",
        // Intel Quick Sync.
        "h264_qsv", "hevc_qsv", "av1_qsv",
        // AMD.
        "h264_amf", "hevc_amf", "av1_amf",
    };

    /// <summary>
    /// Rows look like <c>V....D hevc_nvenc           NVIDIA NVENC hevc encoder</c>. The flags
    /// column starts with the media type, so a video encoder begins with <c>V</c>.
    /// </summary>
    public static IReadOnlyList<string> Parse(string output)
    {
        var found = new List<string>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                continue;
            }

            var name = parts[1];
            if (parts[0].StartsWith('V') && Interesting.Contains(name) && !found.Contains(name))
            {
                found.Add(name);
            }
        }

        return found;
    }

    /// <summary>
    /// Audio encoders Optimisarr can ask for. It emits <c>libopus</c> and <c>libmp3lame</c> for the
    /// Opus and MP3 targets, so a build without them must not claim them: on macOS exactly that
    /// mismatch meant a job was handed over, failed with "Unknown encoder", and came straight back.
    /// </summary>
    public static readonly IReadOnlySet<string> InterestingAudio = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "aac", "libopus", "libmp3lame", "ac3", "eac3", "flac", "alac",
    };

    public static IReadOnlyList<string> ParseAudio(string output)
    {
        var found = new List<string>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                continue;
            }

            var name = parts[1];
            if (parts[0].StartsWith('A') && InterestingAudio.Contains(name) && !found.Contains(name))
            {
                found.Add(name);
            }
        }

        return found;
    }
}
