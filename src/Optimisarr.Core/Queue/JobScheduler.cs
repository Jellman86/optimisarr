using Optimisarr.Core.Domain;

namespace Optimisarr.Core.Queue;

/// <summary>A job waiting to run, with just the facts the scheduler needs.</summary>
public sealed record QueuedJob(
    int Id,
    int? LibraryId,
    int Priority,
    DateTimeOffset EnqueuedAt,
    bool IgnoreMediaActivity = false,
    bool IgnoreLibraryWindow = false,
    WorkPlacement Placement = WorkPlacement.Anywhere,
    /// <summary>
    /// False while a library that chooses a per-title quality has not yet chosen one for this job.
    /// The search runs on the control plane, and no worker may be offered the job until it has, so
    /// this decides whether holding the job for a worker means anything at all.
    /// </summary>
    bool QualityChosen = true,
    MediaKind Kind = MediaKind.Unknown);

public enum WorkloadLane { Video, NonVideo, Evidence }
public sealed record ScheduledJob(int Id, WorkloadLane Lane);

public sealed record WorkloadSlots(int Video, int NonVideo, int Evidence)
{
    public int For(WorkloadLane lane) => lane switch
    {
        WorkloadLane.Video => Video,
        WorkloadLane.NonVideo => NonVideo,
        _ => Evidence
    };

    public WorkloadSlots WithStarted(WorkloadLane lane) => lane switch
    {
        WorkloadLane.Video => this with { Video = Video + 1 },
        WorkloadLane.NonVideo => this with { NonVideo = NonVideo + 1 },
        _ => this with { Evidence = Evidence + 1 }
    };

    public static WorkloadSlots Automatic(int videoSlots, int processors, long availableMemoryBytes) => new(
        Math.Max(1, videoSlots),
        processors >= 8 && availableMemoryBytes >= 8L * 1024 * 1024 * 1024 ? 1 : 0,
        processors >= 4 ? 2 : 1);
}

/// <summary>
/// Decides which queued jobs to start next from bounded local lanes. During playback it admits
/// only jobs carrying an explicit interactive exception. Priority remains dominant; libraries
/// at the same priority take turns across dispatch cycles, with FIFO inside each library.
/// </summary>
public static class JobScheduler
{
    public static WorkloadLane LaneFor(MediaKind kind) => kind is MediaKind.Audio or MediaKind.Image
        ? WorkloadLane.NonVideo : WorkloadLane.Video;

    /// <summary>Fill each local lane independently while retaining priority and FIFO ordering.</summary>
    public static IReadOnlyList<ScheduledJob> SelectJobsByWorkload(
        IReadOnlyList<QueuedJob> queued,
        WorkloadSlots running,
        WorkloadSlots limits,
        bool mediaServicesActive = false,
        int? lastStartedLibraryId = null,
        WorkloadLane? lastStartedVideoClass = null)
    {
        var selected = new List<ScheduledJob>();
        var ordered = FairOrder(queued.Where(job => !mediaServicesActive || job.IgnoreMediaActivity),
            lastStartedLibraryId).ToList();
        var selectedIds = new HashSet<int>();
        // Extra lightweight capacity is independent. Fill it first so audio and image jobs do
        // not unnecessarily occupy the media slot while video work is waiting.
        foreach (var job in ordered.Where(job => LaneFor(job.Kind) == WorkloadLane.NonVideo))
        {
            if (running.NonVideo >= limits.NonVideo) break;
            selected.Add(new ScheduledJob(job.Id, WorkloadLane.NonVideo));
            selectedIds.Add(job.Id);
            running = running.WithStarted(WorkloadLane.NonVideo);
        }

        while (running.Video < limits.Video)
        {
            var remaining = ordered.Where(job => !selectedIds.Contains(job.Id)).ToList();
            if (remaining.Count == 0) break;
            var highestPriority = remaining[0].Priority;
            var next = remaining[0];
            // At equal priority, give the shared media slot to the class that did not use it
            // last. This prevents a single class from monopolising a one-slot installation.
            if (lastStartedVideoClass is WorkloadLane.Video or WorkloadLane.NonVideo)
            {
                var opposite = lastStartedVideoClass == WorkloadLane.Video
                    ? WorkloadLane.NonVideo : WorkloadLane.Video;
                next = remaining.FirstOrDefault(job => job.Priority == highestPriority
                    && LaneFor(job.Kind) == opposite) ?? next;
            }
            selected.Add(new ScheduledJob(next.Id, WorkloadLane.Video));
            selectedIds.Add(next.Id);
            lastStartedVideoClass = LaneFor(next.Kind);
            running = running.WithStarted(WorkloadLane.Video);
        }
        return selected;
    }

    private static IEnumerable<QueuedJob> FairOrder(IEnumerable<QueuedJob> jobs, int? lastStartedLibraryId)
    {
        foreach (var priority in jobs.GroupBy(job => job.Priority).OrderByDescending(group => group.Key))
        {
            var libraries = priority.GroupBy(job => job.LibraryId)
                .Select(group => new Queue<QueuedJob>(group.OrderBy(job => job.EnqueuedAt).ThenBy(job => job.Id)))
                .OrderBy(queue => queue.Peek().EnqueuedAt).ThenBy(queue => queue.Peek().Id)
                .ToList();
            var previous = lastStartedLibraryId is null ? -1
                : libraries.FindIndex(queue => queue.Peek().LibraryId == lastStartedLibraryId);
            if (previous >= 0)
            {
                libraries = [.. libraries.Skip(previous + 1), .. libraries.Take(previous + 1)];
            }
            while (libraries.Count > 0)
            {
                for (var index = 0; index < libraries.Count; index++)
                {
                    yield return libraries[index].Dequeue();
                    if (libraries[index].Count == 0)
                    {
                        libraries.RemoveAt(index);
                        index--;
                    }
                }
            }
        }
    }
    /// <summary>
    /// Whether a queued job may run under its library window. Interactive previews bypass the
    /// automatic background-work window; they remain subject to concurrency, disk, manual pause,
    /// and media-activity gates.
    /// </summary>
    public static bool CanRunInLibraryWindow(
        QueuedJob job,
        TimeOnly? windowStart,
        TimeOnly? windowEnd,
        TimeOnly now) =>
        job.IgnoreLibraryWindow
        || windowStart is null
        || windowEnd is null
        || Scheduling.DispatchPolicyEvaluator.WithinWindow(windowStart.Value, windowEnd.Value, now);

    public static List<QueuedJob> WithinLibraryWindows(
        IEnumerable<QueuedJob> queued,
        IReadOnlyDictionary<int, (TimeOnly Start, TimeOnly End)> autoWindows,
        TimeOnly now) => queued.Where(job =>
        {
            var window = job.LibraryId is { } libraryId
                && autoWindows.TryGetValue(libraryId, out var configuredWindow)
                    ? configuredWindow
                    : ((TimeOnly Start, TimeOnly End)?)null;
            return CanRunInLibraryWindow(job, window?.Start, window?.End, now);
        }).ToList();

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
