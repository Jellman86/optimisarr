using Optimisarr.Core.Calibration;

namespace Optimisarr.Tests;

public sealed class BlindCalibrationPolicyTests
{
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
