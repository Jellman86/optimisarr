using System.Globalization;

namespace Optimisarr.Core.Scheduling;

/// <summary>Whether the worker may start new jobs right now, and why not if it may not.</summary>
public sealed record DispatchDecision(bool CanStart, string? BlockedReason);

/// <summary>
/// Pure policy for whether the queue may start new work: it must be inside the
/// configured processing window, no watched media server may be streaming, and
/// there must be enough free disk. No clocks, no disk I/O, no HTTP — the caller
/// passes the current local time, the measured service-activity signal, and measured
/// free space in, so the decision is deterministic and unit tested. Running jobs are
/// never interrupted by this gate; it only governs starting new ones.
/// </summary>
public static class DispatchPolicyEvaluator
{
    public static DispatchDecision Evaluate(
        bool scheduleEnabled,
        TimeOnly windowStart,
        TimeOnly windowEnd,
        TimeOnly nowLocal,
        long minFreeDiskBytes,
        long? freeDiskBytes,
        bool servicesActive = false,
        string? servicesActiveReason = null)
    {
        if (scheduleEnabled && !WithinWindow(windowStart, windowEnd, nowLocal))
        {
            return new DispatchDecision(false,
                $"Outside the processing window {windowStart:HH:mm}–{windowEnd:HH:mm}.");
        }

        if (servicesActive)
        {
            return new DispatchDecision(false,
                servicesActiveReason ?? "A watched media server is currently active.");
        }

        // A null measurement means we could not read free space; we do not pause on
        // the unknown, because that could halt the queue forever.
        if (minFreeDiskBytes > 0 && freeDiskBytes is { } free && free < minFreeDiskBytes)
        {
            return new DispatchDecision(false,
                $"Free disk space ({Humanize(free)}) is below the {Humanize(minFreeDiskBytes)} minimum.");
        }

        return new DispatchDecision(true, null);
    }

    // A window where start == end means "all day". A window whose end is not after
    // its start crosses midnight (e.g. 22:00–06:00).
    internal static bool WithinWindow(TimeOnly start, TimeOnly end, TimeOnly now)
    {
        if (start == end)
        {
            return true;
        }

        return start < end
            ? now >= start && now < end
            : now >= start || now < end;
    }

    private static string Humanize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Format(CultureInfo.InvariantCulture, "{0:0.#} {1}", value, units[unit]);
    }
}
