using Optimisarr.Core.Calibration;
using Optimisarr.Core.Domain;

namespace Optimisarr.Tests;

public sealed class BlindCalibrationPolicyTests
{
    [Theory]
    [InlineData(false, false, HdrHandling.Exclude, false, true)]
    [InlineData(true, false, HdrHandling.Preserve, true, true)]
    [InlineData(true, false, HdrHandling.Preserve, false, false)]
    [InlineData(true, false, HdrHandling.TonemapToSdr, true, false)]
    [InlineData(true, true, HdrHandling.Preserve, true, false)]
    public void Video_source_readiness_fails_closed_for_unsafe_hdr_presentations(
        bool isHdr,
        bool isDolbyVision,
        HdrHandling hdrHandling,
        bool hdrPlaybackConfirmed,
        bool expected)
    {
        Assert.Equal(
            expected,
            BlindCalibrationPolicy.CanCalibrateVideo(
                isHdr,
                isDolbyVision,
                hdrHandling,
                hdrPlaybackConfirmed));
    }

    [Fact]
    public void Plan_builds_a_most_compressed_first_quality_ladder_and_three_windows()
    {
        var plan = BlindCalibrationPolicy.Plan(durationSeconds: 1_200, currentQuality: 24);

        Assert.Equal([30, 27, 24, 21, 18], plan.RequestedQualities);
        Assert.Equal(
            [
                new CalibrationSample(0, 114, 12),
                new CalibrationSample(1, 594, 12),
                new CalibrationSample(2, 1_074, 12)
            ],
            plan.Samples);
    }

    [Fact]
    public void Plan_clamps_and_deduplicates_quality_edges()
    {
        Assert.Equal([40, 37, 34], BlindCalibrationPolicy.Plan(120, 40).RequestedQualities);
        Assert.Equal([20, 17, 14], BlindCalibrationPolicy.Plan(120, 14).RequestedQualities);
    }

    [Fact]
    public void Plan_rejects_a_source_too_short_for_representative_windows()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            BlindCalibrationPolicy.Plan(47.9, 24));

        Assert.Contains("48 seconds", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Plan_rejects_a_non_finite_duration(double durationSeconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BlindCalibrationPolicy.Plan(durationSeconds, 24));
    }

    [Theory]
    [InlineData("opus", 96, new[] { 64, 80, 96, 128, 160 })]
    [InlineData("aac", 192, new[] { 96, 128, 160, 192, 256 })]
    [InlineData("mp3", 320, new[] { 128, 160, 192, 256, 320 })]
    public void Audio_plan_uses_a_codec_appropriate_most_compressed_first_ladder(
        string codec,
        int currentBitrate,
        int[] expected)
    {
        var plan = BlindCalibrationPolicy.AudioPlan(600, codec, currentBitrate);

        Assert.Equal(expected, plan.RequestedQualities);
        Assert.Equal(3, plan.Samples.Count);
        Assert.All(plan.Samples, sample => Assert.Equal(15, sample.DurationSeconds));
    }

    [Theory]
    [InlineData("flac")]
    [InlineData("copy")]
    public void Audio_plan_rejects_a_target_without_a_meaningful_lossy_bitrate_ladder(string codec)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BlindCalibrationPolicy.AudioPlan(600, codec, 128));
    }

    [Fact]
    public void Audio_level_match_only_attenuates_the_louder_sample()
    {
        var match = BlindCalibrationPolicy.MatchAudioLevels(-18.2, -21.7);

        Assert.Equal(-3.5, match.OriginalGainDb, precision: 6);
        Assert.Equal(0, match.CandidateGainDb);
    }

    [Theory]
    [InlineData(1, 2, CalibrationJudgement.Continue)]
    [InlineData(1, 3, CalibrationJudgement.NoReliableDifference)]
    [InlineData(2, 3, CalibrationJudgement.Distinguishable)]
    public void Screening_uses_three_trials_to_choose_the_next_stage(
        int correct,
        int total,
        CalibrationJudgement expected)
    {
        Assert.Equal(expected, BlindCalibrationPolicy.JudgeScreening(correct, total));
    }

    [Theory]
    [InlineData(8, 9, CalibrationJudgement.Continue)]
    [InlineData(6, 10, CalibrationJudgement.NoReliableDifference)]
    [InlineData(7, 10, CalibrationJudgement.Continue)]
    [InlineData(8, 10, CalibrationJudgement.Continue)]
    [InlineData(9, 10, CalibrationJudgement.Distinguishable)]
    [InlineData(14, 20, CalibrationJudgement.NoReliableDifference)]
    [InlineData(15, 20, CalibrationJudgement.Distinguishable)]
    public void Confirmation_uses_an_inconclusive_extension_before_its_final_judgement(
        int correct,
        int total,
        CalibrationJudgement expected)
    {
        Assert.Equal(expected, BlindCalibrationPolicy.JudgeConfirmation(correct, total));
    }
}
