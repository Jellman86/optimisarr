using Optimisarr.Core.Queue;

namespace Optimisarr.Tests;

/// <summary>
/// The framerate ask from #95, shaped as a cap rather than a target. The only conversion that is
/// ever clean is dropping every other frame — an integer ratio. Anything else, 30→25 or 60→24,
/// repeats a frame every few and judders. So the plan halves the source rate until it is under
/// the cap and refuses everything else, and the same decimation is what both the encode and the
/// VMAF reference apply, so they stay frame-aligned.
/// </summary>
public sealed class FrameRatePlannerTests
{
    [Theory]
    [InlineData(60.0, 30, 30.0, 2)]
    [InlineData(59.94, 30, 29.97, 2)]
    [InlineData(50.0, 30, 25.0, 2)]
    [InlineData(120.0, 30, 30.0, 4)]
    [InlineData(120.0, 60, 60.0, 2)]
    [InlineData(48.0, 30, 24.0, 2)]
    public void The_source_rate_is_halved_until_it_is_under_the_cap(double source, int cap, double expected, int divisor)
    {
        var plan = FrameRatePlanner.Plan(source, cap);

        Assert.NotNull(plan);
        Assert.Equal(expected, plan.TargetFps, precision: 6);
        Assert.Equal(divisor, plan.Divisor);
        Assert.Equal(source, plan.SourceFps);
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
    public void A_variable_frame_rate_source_is_never_decimated()
    {
        // "Every second frame" of an irregular stream means nothing regular, and the encode and
        // the reference could not be relied on to pick the same frames. Left at its own cadence.
        Assert.Null(FrameRatePlanner.Plan(60.0, 30, sourceIsVariableFrameRate: true));
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
    public void The_filter_keeps_frames_by_index_rather_than_by_nearest_timestamp()
    {
        // fps=30 on a 60 fps stream puts every odd frame exactly on its rounding boundary, and
        // which neighbour it keeps then depends on the timebase and on whether timestamps were
        // rebased first. The first real capped encode and its verification disagreed on half the
        // frames that way. An index recovered from the frame's own time has no boundary to fall on.
        var filter = FrameRatePlanner.Filter(new FrameRateDecimation(60, 30, 2));

        Assert.Equal(@"select=not(mod(round(t*60)\,2))", filter);
    }

    [Fact]
    public void The_filter_states_the_exact_source_rate_so_a_fractional_rate_still_rounds_cleanly()
    {
        var filter = FrameRatePlanner.Filter(new FrameRateDecimation(59.94, 29.97, 2));

        Assert.StartsWith("select=not(mod(round(t*59.94)", filter);
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
