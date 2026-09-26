using Optimisarr.Core.Calibration;
using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

/// <summary>
/// Comparing two VMAF models on the same measured windows, so a gate tuned on one can be stated
/// on the other. The study harness feeds this real paired scores; these pin the arithmetic.
/// </summary>
public sealed class VmafModelComparisonTests
{
    private static IReadOnlyList<PairedScore> Line(Func<double, double> candidate) =>
        Enumerable.Range(0, 41).Select(i => 60 + i).Select(v => new PairedScore(v, candidate(v))).ToList();

    [Fact]
    public void An_exact_linear_relationship_maps_every_gate_through_it()
    {
        var comparison = VmafModelComparison.Compare(Line(v => 2 * v - 100), [90, 75])!;

        Assert.Equal(2, comparison.Slope, 6);
        Assert.Equal(-100, comparison.Intercept, 6);
        Assert.Equal(1, comparison.Correlation, 6);
        var gate = comparison.Thresholds[0];
        Assert.Equal(90, gate.BaselineThreshold);
        Assert.Equal(80, gate.LinearEquivalent, 6);
        // Nothing crosses the mapped line differently from the original one.
        Assert.Equal(gate.Windows, gate.Agreements);
    }

    [Fact]
    public void The_rank_equivalent_keeps_the_same_share_of_windows_passing()
    {
        // A monotonic but non-linear relationship: the rank mapping still lands on the gate's image.
        var comparison = VmafModelComparison.Compare(Line(v => v * v / 100), [90])!;

        Assert.Equal(81, comparison.Thresholds[0].RankEquivalent, 0);
    }

    [Fact]
    public void A_candidate_that_scores_everything_lower_shows_as_a_negative_mean_difference()
    {
        var comparison = VmafModelComparison.Compare(Line(v => v - 7), [90])!;

        Assert.Equal(-7, comparison.MeanDifference, 6);
        Assert.Equal(83, comparison.Thresholds[0].LinearEquivalent, 6);
    }

    [Fact]
    public void Too_few_windows_are_not_a_comparison()
    {
        Assert.Null(VmafModelComparison.Compare([new PairedScore(90, 88), new PairedScore(80, 79)], [90]));
    }

    [Fact]
    public void A_chosen_model_replaces_the_automatic_one_in_the_graph()
    {
        var context = new QualityMeasurementContext(1920, 1080, false, false, ModelVersion: "vmaf_v1.0.16_3d0h");

        var command = QualityScoreCommandBuilder.Build("d.mkv", "r.mkv", "l.json", context, 4);

        Assert.Equal("vmaf_v1.0.16_3d0h", command.ModelVersion);
        Assert.Contains("model=version=vmaf_v1.0.16_3d0h:", command.FilterGraph);
        Assert.DoesNotContain("vmaf_v0.6.1", command.FilterGraph);
    }

    [Theory]
    [InlineData("vmaf_v1:log_path=/etc/passwd")]
    [InlineData("vmaf v1")]
    [InlineData("../vmaf")]
    [InlineData("")]
    public void A_model_name_cannot_carry_anything_into_the_filter_graph(string model)
    {
        var context = new QualityMeasurementContext(1920, 1080, false, false, ModelVersion: model);

        Assert.Throws<ArgumentException>(() => QualityScoreCommandBuilder.Build("d.mkv", "r.mkv", "l.json", context, 4));
    }
}
