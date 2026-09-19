using System;

namespace Optimisarr.Sidecar.Tray;

internal static class ActivityIconFrame
{
    internal const int Count = 18;
    private const double SecondsPerTurn = 3;

    internal static int Index(bool working, bool animationEnabled, TimeSpan elapsed)
    {
        if (!working || !animationEnabled) return 0;
        var phase = elapsed.TotalSeconds % SecondsPerTurn / SecondsPerTurn;
        return Math.Min(Count - 1, (int)Math.Floor(phase * Count + 1e-6));
    }
}
