using Optimisarr.Core.Domain;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Replacement;
using Optimisarr.Core.Rules;

namespace Optimisarr.Tests;

public sealed class OutputContainerTests
{
    [Theory]
    [InlineData("Film (2020)/Film (2020).mkv", "mkv")]
    [InlineData("Film (2020)/Film (2020).MKV", "MKV")]
    [InlineData("Show/Show S01E01.Mp4", "Mp4")]
    [InlineData("Show/Season 1/Show S01E01.mp4", "mp4")]
    [InlineData("Old/Clip.m4v", "mp4")]
    [InlineData("Old/Clip.mov", "mp4")]
    [InlineData("Old/Tape.avi", "mkv")]
    [InlineData("Old/Broadcast.ts", "mkv")]
    [InlineData("Old/Broadcast.m2ts", "mkv")]
    [InlineData("Old/Clip.wmv", "mkv")]
    public void Same_as_source_keeps_matroska_and_mp4_and_moves_the_rest_to_a_container_that_holds_the_codecs(string source, string expected) =>
        Assert.Equal(expected, OutputContainer.For(OutputContainer.SameAsSource, source));

    [Theory]
    [InlineData("mp4")]
    [InlineData("mkv")]
    [InlineData(null)]
    public void Any_other_setting_is_left_as_it_is(string? setting) =>
        Assert.Equal(setting, OutputContainer.For(setting, "Film.avi"));

    [Theory]
    [InlineData("source", "source")]
    [InlineData("Source", "Source")]
    [InlineData("mp4", null)]
    [InlineData("mkv", null)]
    [InlineData(null, null)]
    public void Switching_to_a_preset_clears_a_chosen_container_but_keeps_same_as_source(string? setting, string? expected) =>
        Assert.Equal(expected, OutputContainer.AfterPresetChange(setting));

    private static RuleSettings Rules() => RuleProfileDefaults.For(RuleProfile.ConservativeHevc) with { TargetContainer = OutputContainer.SameAsSource };

    private static TranscodeSpec Resolve(string relative, bool imageSubtitles = false, bool mp4IncompatibleAudio = false, RuleSettings? rules = null) =>
        TranscodeSpecResolver.Resolve(rules ?? Rules(), "/data/" + relative, relative, "/work", sourceIsHdr: false, crf: 24, preset: "medium",
            sourceHasImageSubtitles: imageSubtitles, sourceHasMp4IncompatibleAudio: mp4IncompatibleAudio);

    [Fact]
    public void A_matroska_source_keeps_its_name()
    {
        Assert.EndsWith("/Film (2020)/Film (2020).mkv", Resolve("Film (2020)/Film (2020).mkv").OutputPath);
    }

    [Theory]
    [InlineData("Film/Film.MKV")]
    [InlineData("Show/Show S01E01.MP4")]
    public void An_upper_case_extension_keeps_its_spelling_so_the_replacement_lands_on_the_original(string relative)
    {
        var plan = ReplacementPlanner.Plan("/data/" + relative, Resolve(relative).OutputPath, "/trash", DateTimeOffset.UnixEpoch, "1");

        Assert.Equal("/data/" + relative, plan.FinalPath.Replace('\\', '/'));
    }

    [Fact]
    public void An_mp4_source_keeps_its_name()
    {
        Assert.EndsWith("/Show S01E01.mp4", Resolve("Show/Show S01E01.mp4").OutputPath);
    }

    [Fact]
    public void An_mp4_source_whose_copied_audio_mp4_cannot_hold_still_falls_back_to_matroska()
    {
        Assert.EndsWith(".mkv", Resolve("Show/Show S01E01.mp4", mp4IncompatibleAudio: true, rules: Rules() with { VideoAudioCodec = null }).OutputPath);
    }

    [Fact]
    public void Same_as_source_is_not_track_cleanup_so_the_video_is_still_re_encoded()
    {
        Assert.Equal("hevc", Resolve("Film.mkv").VideoCodec);
    }

    [Fact]
    public void A_file_already_in_its_own_container_is_still_a_re_encode_candidate() =>
        Assert.True(CandidateEvaluator.Evaluate(
            new MediaProperties("matroska,webm", "h264", 1920, 1080, 8_000_000_000, false, "Film (2020)/Film (2020).mkv", null, MediaKind.Video, "aac", DurationSeconds: 7200),
            Rules()).IsEligible);

    [Fact]
    public void A_remux_library_keeping_the_source_container_has_nothing_to_remux()
    {
        var remux = RuleProfileDefaults.For(RuleProfile.RemuxCleanup) with { TargetContainer = OutputContainer.SameAsSource };
        var decision = CandidateEvaluator.Evaluate(
            new MediaProperties("matroska,webm", "h264", 1920, 1080, 8_000_000_000, false, "Film.mkv", null, MediaKind.Video, "aac"), remux);

        Assert.False(decision.IsEligible);
        Assert.Contains("mkv", decision.Reason);
    }
}
