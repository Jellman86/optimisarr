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
        Assert.Equal(1920, picture.Width);
        Assert.Equal(1080, picture.Height);
        Assert.Equal(30, picture.FrameRateCap);
        Assert.True(picture.CropBlackBars);
        Assert.True(picture.Deinterlace);
        Assert.Equal(NoiseReduction.Strong, picture.NoiseReduction);
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
