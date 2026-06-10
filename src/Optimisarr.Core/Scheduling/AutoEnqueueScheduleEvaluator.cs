namespace Optimisarr.Core.Scheduling;

/// <summary>
/// Pure policy for whether a library's automatic scan-and-enqueue should run now.
/// It fires once per occurrence of the library's daily window: when the current
/// local time is inside the window and the library has not already run since that
/// window most recently opened. An all-day window (start == end) therefore means
/// "once a day"; a 01:00–06:00 window means "once a night, at 01:00".
///
/// No clocks and no I/O: the caller passes the current local time and the last-run
/// time in, so the decision is deterministic and unit tested. The window semantics
/// (all-day and midnight-crossing) are shared with the dispatch gate via
/// <see cref="DispatchPolicyEvaluator.WithinWindow"/>.
/// </summary>
public static class AutoEnqueueScheduleEvaluator
{
    public static bool IsDue(
        bool enabled,
        TimeOnly windowStart,
        TimeOnly windowEnd,
        DateTime nowLocal,
        DateTime? lastRunLocal)
    {
        if (!enabled)
        {
            return false;
        }

        if (!DispatchPolicyEvaluator.WithinWindow(windowStart, windowEnd, TimeOnly.FromDateTime(nowLocal)))
        {
            return false;
        }

        var mostRecentOpen = MostRecentWindowOpen(windowStart, nowLocal);
        return lastRunLocal is not { } lastRun || lastRun < mostRecentOpen;
    }

    /// <summary>
    /// The instant the current window occurrence opened: today at <paramref name="windowStart"/>
    /// if that is at or before now, otherwise yesterday at that time (the window is still
    /// open from a previous calendar day, e.g. a 22:00–06:00 window seen at 02:00).
    /// </summary>
    internal static DateTime MostRecentWindowOpen(TimeOnly windowStart, DateTime nowLocal)
    {
        var todayOpen = nowLocal.Date + windowStart.ToTimeSpan();
        return todayOpen <= nowLocal ? todayOpen : todayOpen.AddDays(-1);
    }
}
