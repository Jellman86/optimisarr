using Optimisarr.Sidecar.Tray;

namespace Optimisarr.Sidecar.Core.Tests;

public sealed class ActivityIconFrameTests
{
    [Fact]
    public void Index_advances_once_per_tick_and_loops_after_one_precession_cycle()
    {
        Assert.Equal(0, ActivityIconFrame.Index(true, true, TimeSpan.Zero));
        Assert.Equal(1, ActivityIconFrame.Index(true, true, TimeSpan.FromSeconds(0.1)));
        Assert.Equal(87, ActivityIconFrame.Index(true, true, TimeSpan.FromSeconds(8.7)));
        Assert.Equal(0, ActivityIconFrame.Index(true, true, TimeSpan.FromSeconds(8.8)));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Index_stays_on_the_rest_frame_when_work_or_animation_is_disabled(bool working, bool enabled)
    {
        Assert.Equal(0, ActivityIconFrame.Index(working, enabled, TimeSpan.FromSeconds(1.5)));
    }

    [Fact]
    public void Settle_reaches_the_exact_idle_frame_after_six_ticks()
    {
        Assert.Equal(0, ActivityIconFrame.SettleIndex(TimeSpan.Zero));
        Assert.Equal(3, ActivityIconFrame.SettleIndex(TimeSpan.FromSeconds(0.3)));
        Assert.Equal(ActivityIconFrame.SettleFrames, ActivityIconFrame.SettleIndex(TimeSpan.FromSeconds(0.6)));
    }
}
