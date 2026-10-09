namespace Optimisarr.Core.Diagnostics;

public enum DiagnosticSchedulingReason { Idle, Dispatching, ManualPause, MediaActivity, LowDiskSpace, LibraryWindow, WorkerPlacement, ConcurrencyOrLane }

/// <summary>Dispatch counters and stable gates, without watcher identities, titles or paths.</summary>
public sealed record DiagnosticSchedulingSnapshot(DiagnosticSchedulingReason Reason, bool CanStart, int Queued,
    int? WithinWindow, int? Runnable, int? Selected, bool RemoteWorkersOn, bool WorkerAvailable)
{
    public static DiagnosticSchedulingSnapshot Create(bool canStart, bool manuallyPaused, bool activityGate,
        int queued, int? withinWindow, int? runnable, int? selected, bool remoteWorkersOn, bool workerAvailable)
    {
        var reason = !canStart ? manuallyPaused ? DiagnosticSchedulingReason.ManualPause : activityGate
            ? DiagnosticSchedulingReason.MediaActivity : DiagnosticSchedulingReason.LowDiskSpace
            : queued == 0 ? DiagnosticSchedulingReason.Idle : selected > 0 ? DiagnosticSchedulingReason.Dispatching
            : withinWindow == 0 ? DiagnosticSchedulingReason.LibraryWindow : runnable == 0
            ? DiagnosticSchedulingReason.WorkerPlacement : DiagnosticSchedulingReason.ConcurrencyOrLane;
        return new(reason, canStart, queued, withinWindow, runnable, selected, remoteWorkersOn, workerAvailable);
    }
}
