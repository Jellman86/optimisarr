namespace Optimisarr.Core.Queue;

/// <summary>
/// Recognises hardware tone-map setup failures that are safe to retry with the established
/// software colour pipeline. Unrelated failures are left alone so an expensive encode is not
/// repeated when a retry cannot help.
/// </summary>
public static class HardwareToneMapFallback
{
    private static readonly string[] Signatures =
    [
        "mfx_err_unsupported",
        "error initializing a mfx session",
        "error creating a mfx session",
        "function not implemented",
        "no such filter",
        "option not found",
        "failed to configure output pad",
        "failed to create vaapi processing pipeline",
        "failed to start vaapi pipeline"
    ];

    public static bool ShouldRetryInSoftware(string? ffmpegStderr)
    {
        if (string.IsNullOrWhiteSpace(ffmpegStderr))
        {
            return false;
        }

        return Signatures.Any(signature =>
            ffmpegStderr.Contains(signature, StringComparison.OrdinalIgnoreCase));
    }
}
