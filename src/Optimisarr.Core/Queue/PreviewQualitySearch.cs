using Optimisarr.Core.Domain;

namespace Optimisarr.Core.Queue;

/// <summary>
/// Previews are throwaway, so they skip the audio, image and soundtrack quality searches and
/// encode at the library's saved setting. A real job searches for the smallest setting that meets
/// the target, so a preview of such a library must say it is not showing that result.
/// </summary>
public static class PreviewQualitySearch
{
    public static bool UsesSavedSettingInstead(MediaKind kind, bool adaptiveAudio, bool adaptiveImage, bool adaptiveSoundtrack) =>
        kind switch
        {
            MediaKind.Audio => adaptiveAudio,
            MediaKind.Image => adaptiveImage,
            MediaKind.Video => adaptiveSoundtrack,
            _ => false,
        };
}
