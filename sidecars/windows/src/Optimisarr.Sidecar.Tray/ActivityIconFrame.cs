using System;

namespace Optimisarr.Sidecar.Tray;

internal static class ActivityIconFrame
{
    internal const int Count = 88;
    internal const int FramesPerSecond = 10;
    internal const int SettleFrames = 6;
    private const double SecondsPerCycle = 8.8;

    internal static int Index(bool working, bool animationEnabled, TimeSpan elapsed)
    {
        if (!working || !animationEnabled) return 0;
        var phase = elapsed.TotalSeconds % SecondsPerCycle / SecondsPerCycle;
        return Math.Min(Count - 1, (int)Math.Floor(phase * Count + 1e-6));
    }

    internal static int SettleIndex(TimeSpan elapsed) =>
        Math.Min(SettleFrames, Math.Max(0, (int)Math.Floor(elapsed.TotalSeconds * FramesPerSecond + 1e-6)));
}
