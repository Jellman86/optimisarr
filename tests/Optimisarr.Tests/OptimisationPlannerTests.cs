using Optimisarr.Api.Queue;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Rules;

namespace Optimisarr.Tests;

public sealed class OptimisationPlannerTests
{
    private static readonly MediaProperties Episode = new(
        Container: "matroska,webm",
        VideoCodec: "h264",
        Width: 1920,
        Height: 1080,
        SizeBytes: 3_672_000_000,
        IsHdr: false,
        RelativePath: "The Harbour Line/Season 02/The Harbour Line - S02E05 - Low Water.mkv",
        MaxAudioChannels: 6,
        AudioLanguages: ["eng", "fra"],
        SubtitleLanguages: ["eng", "eng", "fra"]);

    private static RuleSettings Rules(RuleProfile profile, RuleOverrides? overrides = null) =>
        RuleResolver.Resolve(profile, overrides ?? RuleOverrides.None);

    [Fact]
    public void Plan_for_a_balanced_library_re_encodes_the_picture_converts_the_sound_and_keeps_every_track()
    {
        var plan = OptimisationPlanner.Plan(Episode, Rules(RuleProfile.ConservativeHevc), crf: 24);

        Assert.Equal(OptimisationPlanner.Encode, plan.Action);
        Assert.Equal("hevc", plan.Picture!.Codec);
        Assert.Equal(24, plan.Picture.Crf);
        Assert.Null(plan.Picture.Width);
        Assert.False(plan.Picture.TonemapToSdr);
        Assert.Equal("aac", plan.Sound!.Codec);
        Assert.False(plan.Sound.DownmixToStereo);
        Assert.All(plan.AudioTracks!, track => Assert.True(track.Kept));
        Assert.All(plan.SubtitleTracks!, track => Assert.True(track.Kept));
    }

    [Fact]
    public void Plan_names_each_track_a_kept_languages_rule_removes_and_keeps_the_rest()
    {
        var rules = Rules(RuleProfile.ConservativeHevc, RuleOverrides.None with
        {
            KeepAudioLanguages = ["eng"],
            KeepSubtitleLanguages = ["fra"],
        });

        var plan = OptimisationPlanner.Plan(Episode, rules, crf: 24);

        Assert.Equal([new PlannedTrack(0, "eng", true), new PlannedTrack(1, "fra", false)], plan.AudioTracks);
        Assert.Equal(
            [new PlannedTrack(0, "eng", false), new PlannedTrack(1, "eng", false), new PlannedTrack(2, "fra", true)],
            plan.SubtitleTracks);
    }

    [Fact]
    public void Plan_does_not_claim_to_know_the_tracks_of_a_file_probed_before_languages_were_captured()
    {
        var unknown = Episode with { AudioLanguages = null, SubtitleLanguages = null };
        var rules = Rules(RuleProfile.ConservativeHevc, RuleOverrides.None with { KeepAudioLanguages = ["eng"] });

        var plan = OptimisationPlanner.Plan(unknown, rules, crf: 24);

        Assert.Null(plan.AudioTracks);
        Assert.Null(plan.SubtitleTracks);
    }

    [Fact]
    public void Plan_keeps_the_file_name_when_the_library_keeps_each_files_container()
    {
        var rules = Rules(RuleProfile.ConservativeHevc, RuleOverrides.None with { TargetContainer = OutputContainer.SameAsSource });

        var plan = OptimisationPlanner.Plan(Episode, rules, crf: 24);

        Assert.Equal("mkv", plan.File.Container);
        Assert.Equal("The Harbour Line - S02E05 - Low Water.mkv", plan.File.FileName);
        Assert.False(plan.File.Renamed);
        Assert.False(plan.File.MayBecomeMatroska);
    }

    [Fact]
    public void Plan_says_a_fixed_mp4_container_renames_the_file_and_may_still_become_matroska()
    {
        var plan = OptimisationPlanner.Plan(Episode, Rules(RuleProfile.ConservativeHevc), crf: 24);

        Assert.Equal("mp4", plan.File.Container);
        Assert.Equal("The Harbour Line - S02E05 - Low Water.mp4", plan.File.FileName);
        Assert.True(plan.File.Renamed);
        Assert.True(plan.File.MayBecomeMatroska);
    }

    [Fact]
    public void Plan_reports_every_picture_change_the_library_asks_for()
    {
        var hdr = Episode with { IsHdr = true, Width = 3840, Height = 2160, FieldOrder = "tt" };
        var rules = Rules(RuleProfile.ConservativeHevc, RuleOverrides.None with
        {
            Hdr = HdrHandling.TonemapToSdr,
            VideoDownscaleHeight = 1080,
            MaxFrameRate = 30,
            CropBlackBars = true,
            DeinterlaceVideo = true,
            NoiseReduction = NoiseReduction.Strong,
        });

        var picture = OptimisationPlanner.Plan(hdr, rules, crf: 22).Picture!;

        Assert.True(picture.TonemapToSdr);
        // Black bars are still to be measured, so the exact new size is not known: only its limit is.
        Assert.Null(picture.Width);
        Assert.Null(picture.Height);
        Assert.Equal(1080, picture.ShrinkToHeight);
        Assert.Equal(30, picture.FrameRateCap);
        Assert.True(picture.CropBlackBars);
        Assert.True(picture.Deinterlace);
        Assert.Equal(NoiseReduction.Strong, picture.NoiseReduction);
    }

    [Fact]
    public void Plan_gives_the_exact_new_size_only_when_no_black_bars_are_to_be_removed()
    {
        var uhd = Episode with { Width = 3840, Height = 2160 };
        var rules = Rules(RuleProfile.ConservativeHevc, RuleOverrides.None with { VideoDownscaleHeight = 1080 });

        var picture = OptimisationPlanner.Plan(uhd, rules, crf: 24).Picture!;
        var untouched = OptimisationPlanner.Plan(Episode, rules, crf: 24).Picture!;

        Assert.Equal(1920, picture.Width);
        Assert.Equal(1080, picture.Height);
        Assert.Equal(1080, picture.ShrinkToHeight);
        Assert.Null(untouched.ShrinkToHeight);
    }

    [Fact]
    public void Plan_writes_matroska_when_the_inventory_already_shows_audio_mp4_cannot_carry()
    {
        var rules = Rules(RuleProfile.ConservativeHevc, RuleOverrides.None with { VideoAudioCodec = "copy" });

        var plan = OptimisationPlanner.Plan(Episode, rules, crf: 24, sourceHasMp4IncompatibleAudio: true);

        Assert.Equal("mkv", plan.File.Container);
        Assert.Equal("The Harbour Line - S02E05 - Low Water.mkv", plan.File.FileName);
        Assert.False(plan.File.Renamed);
        Assert.False(plan.File.MayBecomeMatroska);
    }

    [Fact]
    public void Plan_hedges_about_matroska_only_for_a_file_with_subtitles()
    {
        var withoutSubtitles = Episode with { SubtitleLanguages = [], SubtitleTrackCount = 0 };

        Assert.False(OptimisationPlanner.Plan(withoutSubtitles, Rules(RuleProfile.ConservativeHevc), crf: 24).File.MayBecomeMatroska);
    }

    [Fact]
    public void Plan_copies_the_sound_of_a_job_that_would_be_upscaled_as_the_upscaling_run_does()
    {
        var saved = Rules(RuleProfile.ConservativeHevc, RuleOverrides.None with { DownmixToStereo = true });

        var plan = OptimisationPlanner.Plan(Episode, QueueDispatcher.SuperResolutionRules(saved, enabledForAttempt: true), crf: 24, upscales: true);
        var ordinary = OptimisationPlanner.Plan(Episode, QueueDispatcher.SuperResolutionRules(saved, enabledForAttempt: false), crf: 24);

        Assert.Null(plan.Sound!.Codec);
        Assert.False(plan.Sound.DownmixToStereo);
        Assert.Equal("aac", ordinary.Sound!.Codec);
        Assert.True(ordinary.Sound.DownmixToStereo);
    }

    [Fact]
    public void Plan_says_when_the_job_would_be_upscaled()
    {
        var rules = Rules(RuleProfile.ConservativeHevc);

        Assert.True(OptimisationPlanner.Plan(Episode, rules, crf: 24, upscales: true).Picture!.Upscales);
        Assert.False(OptimisationPlanner.Plan(Episode, rules, crf: 24).Picture!.Upscales);
    }

    [Fact]
    public void Plan_for_an_image_says_its_format_quality_resize_and_whether_it_is_compressed_again()
    {
        var rules = Rules(RuleProfile.ConservativeHevc, RuleOverrides.None with
        {
            TargetImageFormat = "webp", ImageQuality = 70, ReencodeLossyImages = true,
            ImageDownscaleMode = ImageDownscaleMode.MaxLongEdge, ImageDownscaleValue = 3840,
        });
        var png = new MediaProperties("png_pipe", "png", 4000, 3000, 12_000_000, false, "Trip/IMG_0001.png", Kind: MediaKind.Image);
        var jpeg = new MediaProperties("image2", "mjpeg", 4000, 3000, 6_000_000, false, "Trip/IMG_0002.jpg", Kind: MediaKind.Image);

        var lossless = OptimisationPlanner.Plan(png, rules, crf: null).Image!;
        var lossy = OptimisationPlanner.Plan(jpeg, rules, crf: null).Image!;

        Assert.Equal("webp", lossless.Format);
        Assert.Equal(70, lossless.Quality);
        Assert.Equal(ImageDownscaleMode.MaxLongEdge, lossless.ResizeMode);
        Assert.Equal(3840, lossless.ResizeValue);
        Assert.False(lossless.RecompressesLossy);
        Assert.True(lossy.RecompressesLossy);
    }

    [Fact]
    public void Plan_leaves_a_progressive_file_alone_even_when_the_library_deinterlaces()
    {
        var rules = Rules(RuleProfile.ConservativeHevc, RuleOverrides.None with { DeinterlaceVideo = true });

        Assert.False(OptimisationPlanner.Plan(Episode with { FieldOrder = "progressive" }, rules, crf: 24).Picture!.Deinterlace);
    }

    [Fact]
    public void Plan_for_a_remux_or_track_cleanup_has_no_picture_change_and_copies_the_sound()
    {
        var remux = OptimisationPlanner.Plan(Episode, Rules(RuleProfile.RemuxCleanup), crf: null);
        var cleanup = OptimisationPlanner.Plan(
            Episode,
            Rules(RuleProfile.TrackCleanup, RuleOverrides.None with { KeepAudioLanguages = ["eng"] }),
            crf: null);

        Assert.Equal(OptimisationPlanner.Remux, remux.Action);
        Assert.Null(remux.Picture);
        Assert.Null(remux.Sound!.Codec);
        Assert.Equal(OptimisationPlanner.TrackCleanup, cleanup.Action);
        Assert.Null(cleanup.Picture);
        Assert.False(cleanup.File.Renamed);
        Assert.False(cleanup.AudioTracks![1].Kept);
    }

    [Fact]
    public void Plan_for_audio_and_image_files_names_the_target_format()
    {
        var song = new MediaProperties("flac", null, null, null, 30_000_000, false, "Album/01 Track.flac", Kind: MediaKind.Audio, AudioCodec: "flac", MaxAudioChannels: 2);
        var photo = new MediaProperties("png_pipe", "png", 4000, 3000, 12_000_000, false, "Trip/IMG_0001.png", Kind: MediaKind.Image);
        var rules = Rules(RuleProfile.ConservativeHevc, RuleOverrides.None with { TargetAudioCodec = "opus", TargetImageFormat = "webp" });

        var audio = OptimisationPlanner.Plan(song, rules, crf: null);
        var image = OptimisationPlanner.Plan(photo, rules, crf: null);

        Assert.Equal(OptimisationPlanner.Audio, audio.Action);
        Assert.Equal("opus", audio.Sound!.Codec);
        Assert.Equal("01 Track.opus", audio.File.FileName);
        Assert.Equal(OptimisationPlanner.Image, image.Action);
        Assert.Equal("IMG_0001.webp", image.File.FileName);
        Assert.Null(image.Sound);
    }
}
