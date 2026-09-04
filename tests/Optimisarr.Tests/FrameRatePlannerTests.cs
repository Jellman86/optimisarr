using Optimisarr.Core.Queue;

namespace Optimisarr.Tests;

/// <summary>
/// The framerate ask from #95, shaped as a cap rather than a target. The only conversion that is
/// ever clean is dropping every other frame — an integer ratio. Anything else, 30→25 or 60→24,
/// repeats a frame every few and judders. So the plan halves the source rate until it is under
/// the cap and refuses everything else, and the exact result is what both the encode and the VMAF
/// reference are decimated to, so they stay frame-aligned.
/// </summary>
public sealed class FrameRatePlannerTests
{
    [Theory]
    [InlineData(60.0, 30, 30.0)]
    [InlineData(59.94, 30, 29.97)]
    [InlineData(50.0, 30, 25.0)]
    [InlineData(120.0, 30, 30.0)]
    [InlineData(120.0, 60, 60.0)]
    [InlineData(48.0, 30, 24.0)]
    public void The_source_rate_is_halved_until_it_is_under_the_cap(double source, int cap, double expected)
    {
        var target = FrameRatePlanner.Plan(source, cap);

        Assert.NotNull(target);
        Assert.Equal(expected, target.Value, precision: 6);
    }

    [Theory]
    [InlineData(24.0, 30)]
    [InlineData(23.976, 30)]
    [InlineData(25.0, 30)]
    [InlineData(30.0, 30)]
    [InlineData(60.0, 60)]
    public void A_source_at_or_under_the_cap_is_left_alone(double source, int cap)
    {
        // A cap only ever lowers. Raising a rate would invent frames.
        Assert.Null(FrameRatePlanner.Plan(source, cap));
    }

    [Fact]
    public void A_result_that_would_fall_below_cinema_rate_is_refused()
    {
        // 45 fps under a 30 cap halves to 22.5, which is a worse picture than either. There is no
        // clean answer for such a source, so the honest one is not to touch it.
        Assert.Null(FrameRatePlanner.Plan(38.0, 30));
    }

    [Fact]
    public void Unknown_or_absurd_inputs_produce_no_target()
    {
        Assert.Null(FrameRatePlanner.Plan(null, 30));
        Assert.Null(FrameRatePlanner.Plan(60.0, null));
        Assert.Null(FrameRatePlanner.Plan(0.0, 30));
        Assert.Null(FrameRatePlanner.Plan(60.0, 0));
        Assert.Null(FrameRatePlanner.Plan(double.NaN, 30));
    }

    [Fact]
    public void The_filter_states_the_exact_rate()
    {
        // The same value the VMAF reference is decimated to, so the two stay frame-aligned.
        var filter = FrameRatePlanner.Filter(29.97);

        Assert.StartsWith("fps=fps=29.97", filter);
    }

    [Fact]
    public void The_expected_frame_count_shrinks_with_the_decimation()
    {
        // Progress is measured in frames the encoder emits. A 60 → 30 job emits half of them; an
        // estimate left at the source count would sit at 50% when the encode finished.
        Assert.Equal(1_800, FrameRatePlanner.ScaleFrameCount(3_600, 60, 30));
        Assert.Equal(3_600, FrameRatePlanner.ScaleFrameCount(3_600, 60, null));
        Assert.Equal(3_600, FrameRatePlanner.ScaleFrameCount(3_600, null, 30));
        Assert.Null(FrameRatePlanner.ScaleFrameCount(null, 60, 30));
    }
}
