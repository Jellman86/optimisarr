using Optimisarr.Core.Domain;

namespace Optimisarr.Core.Queue;

/// <summary>A quality search a real job would run that a preview did not.</summary>
[Flags]
public enum SkippedQualitySearch
{
    None = 0,
    AudioQuality = 1,
    ImageQuality = 2,
    SoundtrackBitrates = 4,
    VideoQuality = 8,
}

/// <summary>
/// Previews are throwaway, so the audio, image and soundtrack quality searches never run for them,
/// and a preview sampled on a worker also encodes video at the saved quality. A real job in the
/// same library would search first, so the preview records exactly which searches it left out and
/// the panel can say so. A search the real job would not run either is never reported.
/// </summary>
public static class PreviewQualitySearch
{
    /// <param name="soundtrackReencoded">The job re-encodes at least its retained soundtracks.</param>
    /// <param name="adaptiveVideo">A real job of this library would search for its video quality.</param>
    /// <param name="videoSearchRuns">This preview runs that video search itself.</param>
    /// <param name="realJobUpscales">
    /// A real job of this file would run super-resolution, which copies audio and keeps the fixed
    /// video quality, so neither video search would run for it either.
    /// </param>
    public static SkippedQualitySearch Skipped(MediaKind kind, bool adaptiveAudio, bool adaptiveImage,
        bool adaptiveSoundtrack, bool soundtrackReencoded, bool adaptiveVideo, bool videoSearchRuns,
        bool realJobUpscales = false) => kind switch
    {
        MediaKind.Audio when adaptiveAudio => SkippedQualitySearch.AudioQuality,
        MediaKind.Image when adaptiveImage => SkippedQualitySearch.ImageQuality,
        MediaKind.Video when realJobUpscales => SkippedQualitySearch.None,
        MediaKind.Video =>
            (adaptiveSoundtrack && soundtrackReencoded ? SkippedQualitySearch.SoundtrackBitrates : SkippedQualitySearch.None)
            | (adaptiveVideo && !videoSearchRuns ? SkippedQualitySearch.VideoQuality : SkippedQualitySearch.None),
        _ => SkippedQualitySearch.None,
    };

    /// <summary>The skipped searches as stable names for the API, in a fixed order.</summary>
    public static IReadOnlyList<string> Names(SkippedQualitySearch? skipped) =>
        skipped is { } value
            ? Enum.GetValues<SkippedQualitySearch>().Where(flag => flag != SkippedQualitySearch.None && value.HasFlag(flag))
                .Select(flag => flag.ToString()).ToArray()
            : [];
}
