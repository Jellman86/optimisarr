using Optimisarr.Core.Domain;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Rules;

namespace Optimisarr.Tests;

public sealed class AlacContainerPlanningTests
{
    private static readonly RuleSettings CopyAudio = RuleProfileDefaults.For(RuleProfile.ConservativeHevc)
        with { VideoAudioCodec = null };

    [Theory]
    [InlineData("mkv", "mp4")]
    [InlineData("MKA", "mov")]
    [InlineData("mkv", ".m4v")]
    public void Copied_matroska_alac_keeps_a_container_that_preserves_tail_samples(string source, string target)
    {
        var spec = Plan(CopyAudio with { TargetContainer = target }, source, ["alac"]);
        Assert.EndsWith(".mkv", spec.OutputPath);
        Assert.Null(spec.AudioEncoder);
    }

    [Fact]
    public void A_remux_also_preserves_matroska_alac_in_matroska()
        => Assert.EndsWith(".mkv", Plan(CopyAudio with { TargetVideoCodec = null }, "mkv", ["alac"]).OutputPath);

    [Theory]
    [InlineData("mp4", "alac")]
    [InlineData("mov", "alac")]
    [InlineData("mkv", "flac")]
    [InlineData("mkv", "aac")]
    public void Other_proved_copy_paths_keep_the_selected_mp4_target(string source, string codec)
        => Assert.EndsWith(".mp4", Plan(CopyAudio, source, [codec]).OutputPath);

    [Fact]
    public void Re_encoded_audio_can_use_the_selected_mp4_target()
        => Assert.EndsWith(".mp4", Plan(CopyAudio with { VideoAudioCodec = "aac" }, "mkv", ["alac"]).OutputPath);

    [Fact]
    public void Removing_the_alac_track_allows_mp4_without_altering_the_kept_aac_track()
    {
        var spec = Plan(CopyAudio with { KeepAudioLanguages = ["eng"] }, "mkv", ["aac", "alac"], ["eng", "fra"]);
        Assert.EndsWith(".mp4", spec.OutputPath);
        Assert.Equal([1], spec.RemoveAudioStreamIndexes);
    }

    [Fact]
    public void A_kept_alac_track_still_requires_matroska_after_other_tracks_are_removed()
    {
        var spec = Plan(CopyAudio with { KeepAudioLanguages = ["fra"] }, "mkv", ["aac", "alac"], ["eng", "fra"]);
        Assert.EndsWith(".mkv", spec.OutputPath);
        Assert.Equal([0], spec.RemoveAudioStreamIndexes);
    }

    [Theory]
    [InlineData("mkv")]
    [InlineData("mka")]
    public void A_fallback_that_leaves_a_remux_unchanged_is_recognised_before_encoding(string source)
        => Assert.True(AudioContainerCompatibility.CopiedAlacFallbackHasNoWork(
            Plan(CopyAudio with { TargetVideoCodec = null }, source, ["alac"]), ["alac"]));

    [Theory]
    [InlineData("hevc", null)]
    [InlineData(null, "aac")]
    public void A_real_video_or_audio_encode_is_not_cancelled_as_a_no_op(string? video, string? audio)
        => Assert.False(AudioContainerCompatibility.CopiedAlacFallbackHasNoWork(
            new TranscodeSpec("/data/film.mkv", "/work/film.mkv", video, 23, "fast", false, AudioEncoder: audio), ["alac"]));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_remux_that_removes_tracks_still_has_work(bool audio)
        => Assert.False(AudioContainerCompatibility.CopiedAlacFallbackHasNoWork(
            new TranscodeSpec("/data/film.mkv", "/work/film.mkv", null, null, null, false,
                RemoveAudioStreamIndexes: audio ? [1] : null, RemoveSubtitleStreamIndexes: audio ? null : [0]), ["alac", "flac"]));

    [Fact]
    public void An_original_mp4_alac_remux_is_outside_the_matroska_fallback_guard()
        => Assert.False(AudioContainerCompatibility.CopiedAlacFallbackHasNoWork(
            new TranscodeSpec("/data/film.mp4", "/work/film.mp4", null, null, null, false), ["alac"]));

    [Fact]
    public void A_known_unchanged_alac_remux_is_skipped_with_its_reason_before_queueing()
    {
        var decision = CandidateEvaluator.Evaluate(RemuxSource(), CopyAudio with { TargetVideoCodec = null });
        Assert.False(decision.IsEligible);
        Assert.Contains("ALAC", decision.Reason);
    }

    [Fact]
    public void Real_track_removal_keeps_a_matroska_alac_remux_eligible()
        => Assert.True(CandidateEvaluator.Evaluate(RemuxSource() with { AudioLanguages = ["eng", "fra"] },
            CopyAudio with { TargetVideoCodec = null, KeepAudioLanguages = ["eng"] }).IsEligible);

    [Fact]
    public void Unknown_languages_defer_the_remux_decision_to_fresh_planning()
        => Assert.True(CandidateEvaluator.Evaluate(RemuxSource(),
            CopyAudio with { TargetVideoCodec = null, KeepAudioLanguages = ["eng"] }).IsEligible);

    [Fact]
    public void Re_encoding_audio_is_still_an_eligible_change()
        => Assert.True(CandidateEvaluator.Evaluate(RemuxSource(),
            CopyAudio with { TargetVideoCodec = null, VideoAudioCodec = "aac" }).IsEligible);

    private static MediaProperties RemuxSource() => new("matroska,webm", "h264", 320, 180,
        3_000_000_000, false, "film.mkv") { AudioCodecSummary = "flac, alac" };

    private static TranscodeSpec Plan(RuleSettings rules, string source, IReadOnlyList<string> codecs,
        IReadOnlyList<string?>? languages = null) => TranscodeSpecResolver.Resolve(rules,
            $"/data/film.{source}", $"film.{source}", "/work", false, 23, "medium",
            sourceAudioCodecs: codecs, sourceAudioLanguages: languages);
}
