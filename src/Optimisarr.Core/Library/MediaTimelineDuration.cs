using Optimisarr.Core.Domain;

namespace Optimisarr.Core.Library;

/// <summary>Selects the playable timeline used to place samples and report media duration.</summary>
public static class MediaTimelineDuration
{
    public static double? Resolve(
        MediaKind kind,
        double? videoDurationSeconds,
        double? containerDurationSeconds)
    {
        if (kind == MediaKind.Video
            && videoDurationSeconds is > 0
            && double.IsFinite(videoDurationSeconds.Value))
        {
            return videoDurationSeconds;
        }

        return containerDurationSeconds is > 0 && double.IsFinite(containerDurationSeconds.Value)
            ? containerDurationSeconds
            : null;
    }
}
