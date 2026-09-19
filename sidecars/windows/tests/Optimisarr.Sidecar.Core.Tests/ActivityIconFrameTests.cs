using Optimisarr.Sidecar.Tray;

namespace Optimisarr.Sidecar.Core.Tests;

public sealed class ActivityIconFrameTests
{
    [Fact]
    public void Index_advances_once_per_tick_and_loops_after_one_turn()
    {
        Assert.Equal(0, ActivityIconFrame.Index(true, true, TimeSpan.Zero));
        Assert.Equal(1, ActivityIconFrame.Index(true, true, TimeSpan.FromSeconds(1d / 6)));
        Assert.Equal(0, ActivityIconFrame.Index(true, true, TimeSpan.FromSeconds(3)));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Index_stays_on_the_rest_frame_when_work_or_animation_is_disabled(bool working, bool enabled)
    {
        Assert.Equal(0, ActivityIconFrame.Index(working, enabled, TimeSpan.FromSeconds(1.5)));
    }
}
