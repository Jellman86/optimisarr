namespace Optimisarr.Api.Queue;

/// <summary>Selects the picture timeline used to place adaptive VMAF samples.</summary>
internal static class AdaptiveQualityDuration
{
    public static double? Resolve(double? videoDurationSeconds, double? containerDurationSeconds)
    {
        if (videoDurationSeconds is > 0 && double.IsFinite(videoDurationSeconds.Value))
        {
            return videoDurationSeconds;
        }

        return containerDurationSeconds is > 0 && double.IsFinite(containerDurationSeconds.Value)
            ? containerDurationSeconds
            : null;
    }
}
