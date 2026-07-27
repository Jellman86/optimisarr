using Optimisarr.Core.Queue;

namespace Optimisarr.Tests;

public sealed class AdaptiveQualitySearchTests
{
    [Fact]
    public void Search_measures_the_library_quality_first()
    {
        var decision = AdaptiveQualitySearch.Decide(24, []);

        Assert.Equal(24, decision.NextQuality);
        Assert.False(decision.Complete);
    }

    [Fact]
    public void Passing_baseline_searches_toward_a_smaller_output()
    {
        var decision = AdaptiveQualitySearch.Decide(24, [new(24, true, 1_000)]);

        Assert.Equal(30, decision.NextQuality);
    }

    [Fact]
    public void Failing_baseline_searches_toward_higher_quality()
    {
        var decision = AdaptiveQualitySearch.Decide(24, [new(24, false, 1_000)]);

        Assert.Equal(18, decision.NextQuality);
    }

    [Fact]
    public void Search_selects_the_highest_passing_value_from_a_bracket()
    {
        var probes = new List<AdaptiveQualityProbe>
        {
            new(24, true, 1_200),
            new(30, false, 800),
            new(27, true, 950),
            new(28, false, 900)
        };

        var decision = AdaptiveQualitySearch.Decide(24, probes);

        Assert.True(decision.Complete);
        Assert.Equal(27, decision.SelectedQuality);
        Assert.False(decision.FellBack);
    }

    [Fact]
    public void Contradictory_scores_fail_back_to_the_library_quality()
    {
        var decision = AdaptiveQualitySearch.Decide(24,
        [
            new(24, false, 1_200),
            new(30, true, 800)
        ]);

        Assert.True(decision.Complete);
        Assert.Equal(24, decision.SelectedQuality);
        Assert.True(decision.FellBack);
    }

    [Fact]
    public void Search_is_bounded_at_the_encoder_limits()
    {
        Assert.Equal(51, AdaptiveQualitySearch.Decide(49, [new(49, true, 1_000)]).NextQuality);
        Assert.Equal(0, AdaptiveQualitySearch.Decide(2, [new(2, false, 1_000)]).NextQuality);
    }

    [Fact]
    public void Search_selects_the_smallest_measured_passing_output_instead_of_assuming_size()
    {
        var decision = AdaptiveQualitySearch.Decide(24,
        [
            new(24, true, 900),
            new(30, true, 1_000)
        ]);

        Assert.True(decision.Complete);
        Assert.Equal(24, decision.SelectedQuality);
        Assert.False(decision.FellBack);
    }
}
