namespace Optimisarr.Core.Scheduling;

/// <summary>
/// Pure policy for whether a library's automatic optimisation should enqueue work right now.
/// Scanning is handled separately on a global interval, so this is simply: the library has
/// auto-optimise enabled and the current local time falls inside its window. Enqueues are
/// idempotent, so running every tick while in-window picks up newly-eligible files promptly
/// (an all-day window, start == end, means "always"). No clocks or I/O — the caller passes the
/// current local time in — so the decision is deterministic and unit tested. The window
/// semantics (all-day and midnight-crossing) are shared with the dispatch gate via
/// <see cref="DispatchPolicyEvaluator.WithinWindow"/>.
/// </summary>
public static class AutoEnqueueScheduleEvaluator
{
    public static bool ShouldEnqueueNow(
        bool enabled,
        TimeOnly windowStart,
        TimeOnly windowEnd,
        TimeOnly nowLocal) =>
        enabled && DispatchPolicyEvaluator.WithinWindow(windowStart, windowEnd, nowLocal);
}
