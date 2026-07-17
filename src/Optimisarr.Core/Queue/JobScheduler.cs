namespace Optimisarr.Core.Queue;

/// <summary>A job waiting to run, with just the facts the scheduler needs.</summary>
public sealed record QueuedJob(
    int Id,
    int? LibraryId,
    int Priority,
    DateTimeOffset EnqueuedAt,
    bool IgnoreMediaActivity = false);

/// <summary>
/// Decides which queued jobs to start next, given how many are already running, the global
/// concurrency limit, and media-server activity. During playback it admits only jobs carrying an
/// explicit interactive exception. Pure and deterministic so scheduling is fully unit tested.
/// Order is priority (desc), then enqueue time, then id — i.e. highest-priority library first,
/// FIFO within the same priority.
/// </summary>
public static class JobScheduler
{
    public static IReadOnlyList<int> SelectJobsToStart(
        IReadOnlyList<QueuedJob> queued,
        int runningCount,
        int maxConcurrent,
        bool mediaServicesActive = false)
    {
        var freeSlots = maxConcurrent - runningCount;
        if (freeSlots <= 0 || queued.Count == 0)
        {
            return Array.Empty<int>();
        }

        return queued
            .Where(job => !mediaServicesActive || job.IgnoreMediaActivity)
            .OrderByDescending(job => job.Priority)
            .ThenBy(job => job.EnqueuedAt)
            .ThenBy(job => job.Id)
            .Take(freeSlots)
            .Select(job => job.Id)
            .ToList();
    }
}
