using Optimisarr.Core.Queue;

namespace Optimisarr.Tests;

public sealed class AdaptiveQualityProgressTests
{
    [Fact]
    public void Preparation_progress_advances_across_encode_measure_and_windows()
    {
        var encodeHalf = AdaptiveQualityProgress.Map(
            0, 0, 3, AdaptiveQualityPhase.Encoding, 0.5);
        var measureHalf = AdaptiveQualityProgress.Map(
            0, 0, 3, AdaptiveQualityPhase.Measuring, 0.5);
        var nextWindow = AdaptiveQualityProgress.Map(
            0, 1, 3, AdaptiveQualityPhase.Encoding, 0);

        Assert.True(encodeHalf > 0);
        Assert.True(measureHalf > encodeHalf);
        Assert.True(nextWindow > measureHalf);
    }

    [Fact]
    public void Preparation_progress_reserves_the_end_for_the_phase_transition()
    {
        var final = AdaptiveQualityProgress.Map(
            AdaptiveQualitySearch.MaximumProbes - 1,
            2,
            3,
            AdaptiveQualityPhase.Measuring,
            1);

        Assert.Equal(0.999, final);
    }
}
