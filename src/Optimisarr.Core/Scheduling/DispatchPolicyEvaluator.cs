using System.Globalization;

namespace Optimisarr.Core.Scheduling;

/// <summary>Whether the worker may start new jobs right now, and why not if it may not.</summary>
public sealed record DispatchDecision(bool CanStart, string? BlockedReason);

/// <summary>
/// Pure global policy for whether the queue may start new work: no watched media server may be
/// streaming, and there must be enough free disk. <em>When</em> a given library's jobs may run is a
/// per-library concern (its auto-optimise window), applied by the dispatcher with
/// <see cref="WithinWindow"/> — there is no global processing window. No clocks, no disk I/O, no
/// HTTP: the caller passes the measured signals in, so the decision is deterministic and unit
/// tested. Running jobs are never interrupted by this gate; it only governs starting new ones.
/// </summary>
public static class DispatchPolicyEvaluator
{
    public static DispatchDecision Evaluate(
        long minFreeDiskBytes,
        long? freeDiskBytes,
        bool servicesActive = false,
        string? servicesActiveReason = null)
    {
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
    // its start crosses midnight (e.g. 22:00–06:00). Used for per-library auto-optimise windows.
    public static bool WithinWindow(TimeOnly start, TimeOnly end, TimeOnly now)
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
