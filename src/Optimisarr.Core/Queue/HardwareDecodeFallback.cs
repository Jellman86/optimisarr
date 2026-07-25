namespace Optimisarr.Core.Queue;

/// <summary>
/// Decides whether a failed hardware-decode transcode is worth retrying with software
/// decode. Hardware decoders are codec/profile-specific: a source the GPU cannot decode
/// fails fast at initialisation, where the universal software decoder would succeed.
/// Matching is pure string inspection over ffmpeg's stderr tail so it can be unit tested,
/// and deliberately scoped to decode/hwaccel setup failures so an unrelated late failure
/// (e.g. the disk filling mid-encode) is not retried needlessly.
/// </summary>
public static class HardwareDecodeFallback
{
    private static readonly string[] Signatures =
    [
        "hwaccel",
        "failed setup for format",
        "impossible to convert between the formats",
        "error while opening decoder",
        "no decoder surfaces left",
        "device creation failed",
        "no device available for decoder",
        "device setup failed for decoder",
        "decoder not found",
        "no usable decoder",
        "init_hw_device",
    ];

    /// <summary>
    /// True when <paramref name="ffmpegStderr"/> looks like a hardware decode/hwaccel setup
    /// failure that a software-decode retry could recover from.
    /// </summary>
    public static bool ShouldRetryInSoftware(string? ffmpegStderr)
    {
        if (string.IsNullOrWhiteSpace(ffmpegStderr))
        {
            return false;
        }

        foreach (var signature in Signatures)
        {
            if (ffmpegStderr.Contains(signature, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
