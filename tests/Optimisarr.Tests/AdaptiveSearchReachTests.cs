using Optimisarr.Core.Queue;

namespace Optimisarr.Tests;

/// <summary>
/// How far the search looks before it stops looking.
///
/// <para>The bracket's far end used to be a ceiling as well as a starting width, so a search whose
/// far end <em>passed</em> concluded there. Measured across seven episodes on an RTX 4070, that end
/// cleared a gate of 85 with a harmonic mean of 96 and less than half the bytes — every job settled
/// at exactly baseline + 6, and the bound was deciding the answer rather than the title.</para>
/// </summary>
public sealed class AdaptiveSearchReachTests
{
    private static AdaptiveQualityProbe Pass(int quality, long bytes) => new(quality, true, bytes);

    private static AdaptiveQualityProbe Fail(int quality, long bytes) => new(quality, false, bytes);

    [Fact]
    public void A_far_end_that_passes_is_not_the_answer_it_is_a_reason_to_look_further()
    {
        // The exact shape seen on every PICARD job: the library's 20 passes, 26 passes at half the
        // size, and the search used to stop there with eleven points of headroom unspent.
        var decision = AdaptiveQualitySearch.Decide(20, [Pass(20, 61_000_000), Pass(26, 28_000_000)]);

        Assert.False(decision.Complete);
        Assert.Equal(32, decision.NextQuality);
    }

    [Fact]
    public void The_reach_doubles_rather_than_creeping()
    {
        // Four probes is the budget. A linear walk outward would spend all of it moving six at a
        // time; doubling finds the edge in two or three and leaves the rest for bisecting back.
        Assert.Equal(26, AdaptiveQualitySearch.Decide(20, [Pass(20, 60)]).NextQuality);
        Assert.Equal(32, AdaptiveQualitySearch.Decide(20, [Pass(20, 60), Pass(26, 30)]).NextQuality);
        Assert.Equal(
            44,
            AdaptiveQualitySearch.Decide(20, [Pass(20, 60), Pass(26, 30), Pass(32, 20)]).NextQuality);
    }

    [Fact]
    public void Reaching_out_stops_where_the_codec_range_does()
    {
        // 51 is the end of the common range. A search that walked past it would ask for a quality
        // no encoder here accepts.
        var decision = AdaptiveQualitySearch.Decide(48, [Pass(48, 60), Pass(51, 30)]);

        Assert.True(decision.Complete);
        Assert.Equal(51, decision.SelectedQuality);
        Assert.False(decision.FellBack);
    }

    [Fact]
    public void A_step_out_that_saves_nothing_ends_the_reach()
    {
        // A larger quality value usually means a smaller file, but only usually — the whole search
        // exists because the encoder's size ordering cannot be assumed. When the step out passed
        // and came back no smaller, the title is not responding to the number and another sample
        // would be spent to learn nothing.
        var decision = AdaptiveQualitySearch.Decide(24, [Pass(24, 900), Pass(30, 1_000)]);

        Assert.True(decision.Complete);
        Assert.Equal(24, decision.SelectedQuality);
    }

    [Fact]
    public void A_step_out_that_fails_gives_the_bisection_its_upper_bound()
    {
        // The reach is only ever taken while everything measured has passed, so there is always a
        // passing anchor to come back to. One failed sample buys the bracket.
        var decision = AdaptiveQualitySearch.Decide(
            20, [Pass(20, 60), Pass(26, 30), Fail(38, 12)]);

        Assert.False(decision.Complete);
        Assert.Equal(32, decision.NextQuality);
    }

    [Fact]
    public void The_budget_still_ends_the_search()
    {
        // Reaching further must not become reaching for ever. Four measured candidates is the
        // bound, and each one is three sample encodes and three VMAF passes on somebody's machine.
        var decision = AdaptiveQualitySearch.Decide(
            20, [Pass(20, 60), Pass(26, 30), Pass(32, 20), Pass(44, 15)]);

        Assert.True(decision.Complete);
        Assert.Equal(44, decision.SelectedQuality);
        Assert.False(decision.FellBack);
    }

    [Fact]
    public void A_baseline_that_fails_still_looks_inward_only()
    {
        // Nothing here changes for a title the library's quality is already too coarse for: the
        // search brackets towards better quality and never reaches outward at all.
        var decision = AdaptiveQualitySearch.Decide(20, [Fail(20, 60)]);

        Assert.False(decision.Complete);
        Assert.Equal(14, decision.NextQuality);
    }

    [Fact]
    public void Reaching_never_passes_a_failure_it_has_already_seen()
    {
        // Non-monotonic evidence — a pass above a failure — already falls back to the library's
        // quality, and reaching outward must not create a path around that.
        var decision = AdaptiveQualitySearch.Decide(20, [Fail(20, 60), Pass(26, 30)]);

        Assert.True(decision.Complete);
        Assert.True(decision.FellBack);
        Assert.Equal(20, decision.SelectedQuality);
    }
}
