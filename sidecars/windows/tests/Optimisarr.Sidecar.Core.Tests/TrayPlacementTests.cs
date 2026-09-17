using Optimisarr.Sidecar.Tray;

namespace Optimisarr.Sidecar.Core.Tests;

public sealed class TrayPlacementTests
{
    [Fact]
    public void Expanding_and_collapsing_preserves_the_taskbar_anchor()
    {
        var screen = new PixelRect(0, 0, 2560, 1440);
        var work = new PixelRect(0, 0, 2560, 1380);
        var compact = TrayPlacement.Place(screen, work, 615, 690);
        var expanded = TrayPlacement.Place(screen, work, 615, 1080);
        Assert.Equal(compact.Top + 690, expanded.Top + 1080);
        Assert.Equal(1376, compact.Top + 690);
        Assert.Equal(compact, TrayPlacement.Place(screen, work, 615, 690));
    }

    [Fact]
    public void Negative_monitor_coordinates_remain_physical_pixels()
    {
        var frame = TrayPlacement.Place(new(-1920, 0, 0, 1080), new(-1920, 0, 0, 1040), 410, 600);
        Assert.Equal(-414, frame.Left);
        Assert.Equal(436, frame.Top);
    }

    [Fact]
    public void Top_and_left_taskbars_keep_the_panel_on_their_edge()
    {
        Assert.Equal(44, TrayPlacement.Place(new(0, 0, 1920, 1080), new(0, 40, 1920, 1080), 410, 600).Top);
        Assert.Equal(64, TrayPlacement.Place(new(0, 0, 1920, 1080), new(60, 0, 1920, 1080), 410, 600).Left);
    }
}
