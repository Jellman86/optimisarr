using System.Globalization;

namespace Optimisarr.Core.Scheduling;

/// <summary>
/// Explains why a queue with work in it still isn't starting anything, when the only thing holding
/// it back is per-library auto-optimise windows being closed. Dispatch can be "ready" (disk and
/// activity gates pass) yet start nothing because every queued job belongs to a library whose
/// window is shut — that is invisible otherwise, so the Queue page can surface this reason. Pure so
/// it is unit tested.
/// </summary>
public static class QueueWaitReason
{
    /// <summary>A library's queued backlog and its auto-optimise window (null window = runs anytime).</summary>
    public sealed record LibraryQueue(string Name, int QueuedCount, TimeOnly? WindowStart, TimeOnly? WindowEnd);

    /// <summary>
    /// A human sentence naming the libraries whose closed windows are gating the queue, or null when
    /// something could run now (or nothing is queued). If any queued library can run now, the queue
    /// is not window-blocked and this returns null.
    /// </summary>
    public static string? Describe(IReadOnlyList<LibraryQueue> queues, TimeOnly now)
    {
        var blocked = new List<LibraryQueue>();
        foreach (var queue in queues)
        {
            if (queue.QueuedCount <= 0)
            {
                continue;
            }

            // No window, or an open window, means this library could start a job right now — so the
            // queue isn't stalled on windows at all.
            if (queue.WindowStart is not { } start
                || queue.WindowEnd is not { } end
                || DispatchPolicyEvaluator.WithinWindow(start, end, now))
            {
                return null;
            }

            blocked.Add(queue);
        }

        if (blocked.Count == 0)
        {
            return null;
        }

        var total = blocked.Sum(b => b.QueuedCount);
        var biggest = blocked.OrderByDescending(b => b.QueuedCount).ThenBy(b => b.Name).First();
        var window = $"{Format(biggest.WindowStart)}–{Format(biggest.WindowEnd)}";

        return total == biggest.QueuedCount
            ? $"{total} job(s) waiting for the {biggest.Name} optimise window ({window})"
            : $"{total} job(s) waiting for their library's optimise window (e.g. {biggest.Name} {window})";
    }

    private static string Format(TimeOnly? time) =>
        time?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "";
}
