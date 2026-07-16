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
    public void Plan_keeps_five_distinct_candidates_at_quality_edges()
    {
        Assert.Equal([40, 37, 34, 31, 28], BlindCalibrationPolicy.Plan(120, 40).RequestedQualities);
        Assert.Equal([26, 23, 20, 17, 14], BlindCalibrationPolicy.Plan(120, 14).RequestedQualities);
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
    [InlineData("opus", new[] { 64, 80, 96, 128, 160 })]
    [InlineData("aac", new[] { 96, 128, 160, 192, 256 })]
    [InlineData("mp3", new[] { 128, 160, 192, 256, 320 })]
    public void Audio_plan_uses_a_codec_appropriate_most_compressed_first_ladder(
        string codec,
        int[] expected)
    {
        var plan = BlindCalibrationPolicy.AudioPlan(600, codec);

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
            BlindCalibrationPolicy.AudioPlan(600, codec));
    }

    [Fact]
    public void Audio_level_match_only_attenuates_the_louder_sample()
    {
        var match = BlindCalibrationPolicy.MatchAudioLevels(-18.2, -21.7);

        Assert.Equal(-3.5, match.OriginalGainDb, precision: 6);
        Assert.Equal(0, match.CandidateGainDb);
    }

    [Fact]
    public void Audio_lineup_uses_one_common_quietest_loudness_target()
    {
        var gains = BlindCalibrationPolicy.MatchAudioGroupLevels([-18.2, -21.7, -20.2]);

        Assert.Equal([-3.5, 0, -1.5], gains);
    }

    [Fact]
    public void Image_plan_uses_a_low_to_high_quality_ladder_and_one_shared_view()
    {
        var plan = BlindCalibrationPolicy.ImagePlan();

        Assert.Equal([40, 55, 70, 82, 92], plan.RequestedQualities);
        Assert.Equal([new CalibrationSample(0, 0, 0)], plan.Samples);
    }

    [Fact]
    public void Preference_result_selects_the_most_compressed_acceptable_quality()
    {
        var plan = BlindCalibrationPolicy.Plan(1_200, 24);
        var ratings = new Dictionary<int, CalibrationPreference>
        {
            [30] = CalibrationPreference.VisiblyWorse,
            [27] = CalibrationPreference.Acceptable,
            [24] = CalibrationPreference.Indistinguishable,
            [21] = CalibrationPreference.Indistinguishable,
            [18] = CalibrationPreference.Indistinguishable
        };

        Assert.Equal(27, BlindCalibrationPolicy.Recommend(plan, ratings));
    }

    [Fact]
    public void Preference_result_keeps_the_current_setting_when_every_encode_is_rejected()
    {
        var plan = BlindCalibrationPolicy.ImagePlan();
        var ratings = plan.RequestedQualities.ToDictionary(
            quality => quality,
            _ => CalibrationPreference.VisiblyWorse);

        Assert.Null(BlindCalibrationPolicy.Recommend(plan, ratings));
    }
}
