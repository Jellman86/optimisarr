using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

public sealed class VmafWindowPlannerTests
{
    [Fact]
    public void Clip_mode_samples_early_middle_and_late_content()
    {
        var windows = VmafWindowPlanner.Plan(durationSeconds: 2_400, enabled: true);

        Assert.Equal(3, windows.Count);
        Assert.Equal(new VmafWindow(220, 40), windows[0]);
        Assert.Equal(new VmafWindow(1_180, 40), windows[1]);
        Assert.Equal(new VmafWindow(2_140, 40), windows[2]);
    }

    [Theory]
    [InlineData(150, true)]
    [InlineData(2400, false)]
    public void Short_or_disabled_measurements_use_the_full_file(double duration, bool enabled)
    {
        Assert.Equal([VmafWindow.Full], VmafWindowPlanner.Plan(duration, enabled));
    }

    [Fact]
    public void Adaptive_samples_are_bounded_and_deterministic()
    {
        Assert.Equal(
            [new VmafWindow(220, 40), new VmafWindow(1_180, 40), new VmafWindow(2_140, 40)],
            VmafWindowPlanner.PlanAdaptive(2_400));
    }

    [Fact]
    public void Adaptive_sampling_deduplicates_overlapping_short_title_windows()
    {
        Assert.Equal([new VmafWindow(0, 30)], VmafWindowPlanner.PlanAdaptive(30));
        Assert.Empty(VmafWindowPlanner.PlanAdaptive(0));
    }
}
