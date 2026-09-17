using System;

namespace Optimisarr.Sidecar.Tray;

internal readonly record struct PixelRect(double Left, double Top, double Right, double Bottom);

internal static class TrayPlacement
{
    // Keep native screen coordinates in pixels; scaling absolute desktop coordinates as DIPs
    // moves the window onto the wrong monitor when adjacent displays have different scaling.
    public static (double Left, double Top) Place(PixelRect screen, PixelRect work, double width, double height)
    {
        const double gap = 4;
        var left = work.Left > screen.Left ? work.Left + gap : work.Right - width - gap;
        var top = work.Top > screen.Top ? work.Top + gap : work.Bottom - height - gap;
        return (Math.Max(work.Left + gap, left), Math.Max(work.Top + gap, top));
    }
}
