using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

public sealed class QualityScoreAggregatorTests
{
    [Fact]
    public void Combines_windows_with_conservative_tail_scores()
    {
        var result = QualityScoreAggregator.Combine([
            QualityResult.Ok(Scores(mean: 94, harmonic: 93, minimum: 50, fifth: 88, frames: 100)),
            QualityResult.Ok(Scores(mean: 90, harmonic: 88, minimum: 42, fifth: 75, frames: 200)),
            QualityResult.Ok(Scores(mean: 92, harmonic: 91, minimum: 47, fifth: 82, frames: 100))
        ], "Three 40-second samples (early, middle and late)");

        Assert.True(result.Measured);
        Assert.Equal(91.5, result.Scores!.VmafMean);
        Assert.Equal(42, result.Scores.VmafMin);
        Assert.Equal(75, result.Scores.VmafFifthPercentile);
        Assert.Equal(400, result.Scores.FrameCount);
        Assert.Equal(
            "SDR; Three 40-second samples (early, middle and late)",
            result.Scores.Preprocessing);
    }

    [Fact]
    public void Propagates_a_failed_window()
    {
        var result = QualityScoreAggregator.Combine([
            QualityResult.Ok(Scores(94, 93, 50, 88, 100)),
            QualityResult.Failed("middle sample failed")
        ], "samples");

        Assert.False(result.Measured);
        Assert.Equal("middle sample failed", result.Error);
    }

    private static QualityScores Scores(double mean, double harmonic, double minimum, double fifth, int frames) =>
        new(mean, harmonic, minimum, null, null, "vmaf_v0.6.1", "SDR", fifth, frames);
}
