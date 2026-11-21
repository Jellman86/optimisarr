using Optimisarr.Core.Domain;
using Optimisarr.Core.Queue;

namespace Optimisarr.Tests;

public sealed class PreviewQualitySearchTests
{
    private static SkippedQualitySearch Skipped(MediaKind kind, bool audio = false, bool image = false, bool soundtrack = false,
        bool reencoded = true, bool video = false, bool videoRuns = true) =>
        PreviewQualitySearch.Skipped(kind, audio, image, soundtrack, reencoded, video, videoRuns);

    [Fact]
    public void Audio_and_image_previews_report_their_own_skipped_search_only()
    {
        Assert.Equal(SkippedQualitySearch.AudioQuality, Skipped(MediaKind.Audio, audio: true, image: true, soundtrack: true));
        Assert.Equal(SkippedQualitySearch.ImageQuality, Skipped(MediaKind.Image, audio: true, image: true, soundtrack: true));
        Assert.Equal(SkippedQualitySearch.None, Skipped(MediaKind.Audio, image: true));
    }

    [Fact]
    public void A_video_preview_reports_soundtrack_selection_only_when_real_jobs_would_re_encode_the_audio()
    {
        Assert.Equal(SkippedQualitySearch.SoundtrackBitrates, Skipped(MediaKind.Video, soundtrack: true));
        Assert.Equal(SkippedQualitySearch.None, Skipped(MediaKind.Video, soundtrack: true, reencoded: false));
    }

    [Fact]
    public void A_local_video_preview_that_runs_its_own_quality_search_does_not_report_it()
    {
        Assert.Equal(SkippedQualitySearch.None, Skipped(MediaKind.Video, video: true, videoRuns: true));
        Assert.Equal(SkippedQualitySearch.VideoQuality | SkippedQualitySearch.SoundtrackBitrates,
            Skipped(MediaKind.Video, soundtrack: true, video: true, videoRuns: false));
    }

    [Fact]
    public void A_preview_of_a_file_real_jobs_would_upscale_reports_no_video_search() =>
        Assert.Equal(SkippedQualitySearch.None,
            PreviewQualitySearch.Skipped(MediaKind.Video, false, false, adaptiveSoundtrack: true, soundtrackReencoded: true,
                adaptiveVideo: true, videoSearchRuns: false, realJobUpscales: true));

    [Fact]
    public void A_new_attempt_starts_without_the_previous_attempts_notes()
    {
        var job = new Optimisarr.Data.Job { SkippedQualitySearches = SkippedQualitySearch.VideoQuality };
        Optimisarr.Api.Queue.QueueDispatcher.PrepareForAttempt(job, DateTimeOffset.UtcNow);
        Assert.Null(job.SkippedQualitySearches);
    }

    [Fact]
    public void Unknown_media_never_reports_a_skipped_search() =>
        Assert.Equal(SkippedQualitySearch.None, Skipped(MediaKind.Unknown, true, true, true, true, true, false));

    [Fact]
    public void Names_are_stable_and_ordered()
    {
        Assert.Equal(["SoundtrackBitrates", "VideoQuality"],
            PreviewQualitySearch.Names(SkippedQualitySearch.VideoQuality | SkippedQualitySearch.SoundtrackBitrates));
        Assert.Empty(PreviewQualitySearch.Names(null));
        Assert.Empty(PreviewQualitySearch.Names(SkippedQualitySearch.None));
    }
}
