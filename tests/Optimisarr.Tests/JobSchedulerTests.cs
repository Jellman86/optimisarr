using Optimisarr.Core.Queue;
using Optimisarr.Core.Domain;

namespace Optimisarr.Tests;

public sealed class JobSchedulerTests
{
    [Fact]
    public void Evidence_and_audio_have_their_own_bounded_slots_beside_a_full_video_lane()
    {
        var t = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var jobs = new[]
        {
            new QueuedJob(1, 1, 2, t, Kind: MediaKind.Video),
            new QueuedJob(2, 2, 1, t, Kind: MediaKind.Audio),
            new QueuedJob(3, 3, 0, t, Kind: MediaKind.Image),
        };
        var selected = JobScheduler.SelectJobsByWorkload(jobs,
            new WorkloadSlots(Video: 1, NonVideo: 0, Evidence: 1),
            new WorkloadSlots(Video: 1, NonVideo: 1, Evidence: 2));
        Assert.Equal([new ScheduledJob(2, WorkloadLane.NonVideo)], selected);
        Assert.Equal(WorkloadLane.Video, JobScheduler.LaneFor(MediaKind.Unknown));
    }

    [Fact]
    public void Audio_and_images_still_use_the_video_slot_when_no_extra_lane_is_enabled()
    {
        var jobs = new[] { new QueuedJob(1, 1, 0, T0, Kind: MediaKind.Audio),
            new QueuedJob(2, 2, 0, T0, Kind: MediaKind.Image) };
        Assert.Equal([new ScheduledJob(1, WorkloadLane.Video)],
            JobScheduler.SelectJobsByWorkload(jobs, new WorkloadSlots(0, 0, 0),
                new WorkloadSlots(1, 0, 1)));
    }

    [Fact]
    public void Automatic_non_video_capacity_is_conservative_on_a_small_container()
    {
        Assert.Equal(new WorkloadSlots(1, 0, 1), WorkloadSlots.Automatic(1, 2, 4L << 30));
        Assert.Equal(new WorkloadSlots(2, 1, 2), WorkloadSlots.Automatic(2, 8, 8L << 30));
    }

    [Fact]
    public void Workload_selection_preserves_priority_and_fifo_within_each_lane()
    {
        var jobs = new[]
        {
            new QueuedJob(1, 1, 1, T0, Kind: MediaKind.Video),
            new QueuedJob(2, 1, 9, T0.AddMinutes(1), Kind: MediaKind.Audio),
            new QueuedJob(3, 1, 9, T0, Kind: MediaKind.Image),
            new QueuedJob(4, 1, 1, T0.AddMinutes(1), Kind: MediaKind.Video),
        };
        Assert.Equal([new ScheduledJob(3, WorkloadLane.NonVideo), new ScheduledJob(2, WorkloadLane.Video)],
            JobScheduler.SelectJobsByWorkload(jobs, new WorkloadSlots(0, 0, 0),
                new WorkloadSlots(1, 1, 1)));
    }

    [Fact]
    public void Media_activity_gates_all_local_lanes_except_explicit_bypasses()
    {
        var jobs = new[]
        {
            new QueuedJob(1, 1, 1, T0, Kind: MediaKind.Video),
            new QueuedJob(2, 1, 1, T0, Kind: MediaKind.Audio),
            new QueuedJob(3, null, 1, T0, IgnoreMediaActivity: true, Kind: MediaKind.Image),
        };
        Assert.Equal([new ScheduledJob(3, WorkloadLane.NonVideo)],
            JobScheduler.SelectJobsByWorkload(jobs, new WorkloadSlots(0, 0, 0),
                new WorkloadSlots(1, 1, 1), mediaServicesActive: true));
    }

    [Fact]
    public void Single_slot_rotates_between_libraries_of_equal_priority_across_dispatch_cycles()
    {
        var queued = new[]
        {
            new QueuedJob(1, 1, 0, T0, Kind: MediaKind.Video),
            new QueuedJob(2, 1, 0, T0.AddMinutes(1), Kind: MediaKind.Video),
            new QueuedJob(3, 2, 0, T0.AddMinutes(2), Kind: MediaKind.Video),
        };
        var slots = new WorkloadSlots(1, 0, 1);
        Assert.Equal([new ScheduledJob(1, WorkloadLane.Video)],
            JobScheduler.SelectJobsByWorkload(queued, new WorkloadSlots(0, 0, 0), slots));
        Assert.Equal([new ScheduledJob(3, WorkloadLane.Video)],
            JobScheduler.SelectJobsByWorkload(queued[1..], new WorkloadSlots(0, 0, 0), slots,
                lastStartedLibraryId: 1));
    }

    [Fact]
    public void Shared_media_slot_rotates_between_video_and_non_video_at_equal_priority()
    {
        var queued = new[]
        {
            new QueuedJob(1, 1, 0, T0, Kind: MediaKind.Video),
            new QueuedJob(2, 1, 0, T0.AddMinutes(1), Kind: MediaKind.Video),
            new QueuedJob(3, 1, 0, T0.AddMinutes(2), Kind: MediaKind.Audio),
        };
        var slots = new WorkloadSlots(1, 0, 1);
        Assert.Equal([new ScheduledJob(1, WorkloadLane.Video)],
            JobScheduler.SelectJobsByWorkload(queued, new WorkloadSlots(0, 0, 0), slots));
        Assert.Equal([new ScheduledJob(3, WorkloadLane.Video)],
            JobScheduler.SelectJobsByWorkload(queued[1..], new WorkloadSlots(0, 0, 0), slots,
                lastStartedVideoClass: WorkloadLane.Video));
        Assert.Equal([new ScheduledJob(2, WorkloadLane.Video)],
            JobScheduler.SelectJobsByWorkload(queued[1..], new WorkloadSlots(0, 0, 0), slots,
                lastStartedVideoClass: WorkloadLane.NonVideo));
    }

    [Fact]
    public void Shared_media_slot_still_prefers_higher_priority_over_class_rotation()
    {
        var queued = new[]
        {
            new QueuedJob(1, 1, 9, T0, Kind: MediaKind.Video),
            new QueuedJob(2, 1, 1, T0.AddMinutes(1), Kind: MediaKind.Audio),
        };
        Assert.Equal([new ScheduledJob(1, WorkloadLane.Video)],
            JobScheduler.SelectJobsByWorkload(queued, new WorkloadSlots(0, 0, 0),
                new WorkloadSlots(1, 0, 1), lastStartedVideoClass: WorkloadLane.Video));
    }

    [Fact]
    public void Dispatch_and_lane_status_exclude_closed_windows_but_keep_manual_unwindowed_and_preview_jobs()
    {
        var jobs = new[]
        {
            new QueuedJob(1, 1, 0, T0, Kind: MediaKind.Video),
            new QueuedJob(2, 2, 0, T0, Kind: MediaKind.Audio),
            new QueuedJob(3, 1, 0, T0, IgnoreLibraryWindow: true, Kind: MediaKind.Video),
        };
        var windows = new Dictionary<int, (TimeOnly Start, TimeOnly End)>
        {
            [1] = (new TimeOnly(1, 0), new TimeOnly(6, 0))
        };
        Assert.Equal([2, 3], JobScheduler.WithinLibraryWindows(jobs, windows, new TimeOnly(12, 0))
            .Select(job => job.Id).ToArray());
        Assert.Equal([1, 2, 3], JobScheduler.WithinLibraryWindows(jobs, windows, new TimeOnly(3, 0))
            .Select(job => job.Id).ToArray());
    }
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static QueuedJob Job(int id, int priority = 0, int enqueuedMinutes = 0) =>
        new(id, LibraryId: 1, priority, T0.AddMinutes(enqueuedMinutes));

    [Fact]
    public void Fills_only_the_free_slots()
    {
        var queued = new[] { Job(1), Job(2), Job(3) };

        var started = JobScheduler.SelectJobsToStart(queued, runningCount: 1, maxConcurrent: 2);

        Assert.Single(started);
    }

    [Fact]
    public void Starts_nothing_when_at_capacity()
    {
        var queued = new[] { Job(1), Job(2) };

        var started = JobScheduler.SelectJobsToStart(queued, runningCount: 2, maxConcurrent: 2);

        Assert.Empty(started);
    }

    [Fact]
    public void Higher_priority_is_started_first()
    {
        var queued = new[]
        {
            Job(1, priority: 0, enqueuedMinutes: 0),
            Job(2, priority: 5, enqueuedMinutes: 10), // newer but higher priority
        };

        var started = JobScheduler.SelectJobsToStart(queued, runningCount: 0, maxConcurrent: 1);

        Assert.Equal(new[] { 2 }, started);
    }

    [Fact]
    public void Ties_break_by_enqueue_order_then_id()
    {
        var queued = new[]
        {
            Job(3, priority: 1, enqueuedMinutes: 5),
            Job(1, priority: 1, enqueuedMinutes: 0),
            Job(2, priority: 1, enqueuedMinutes: 0),
        };

        var started = JobScheduler.SelectJobsToStart(queued, runningCount: 0, maxConcurrent: 3);

        Assert.Equal(new[] { 1, 2, 3 }, started);
    }

    [Fact]
    public void Returns_empty_when_nothing_is_queued()
    {
        var started = JobScheduler.SelectJobsToStart(Array.Empty<QueuedJob>(), runningCount: 0, maxConcurrent: 4);

        Assert.Empty(started);
    }

    [Fact]
    public void Treats_concurrency_below_one_as_paused()
    {
        var queued = new[] { Job(1) };

        var started = JobScheduler.SelectJobsToStart(queued, runningCount: 0, maxConcurrent: 0);

        Assert.Empty(started);
    }

    [Fact]
    public void Active_media_streams_allow_only_jobs_with_an_explicit_bypass()
    {
        var queued = new[]
        {
            new QueuedJob(1, LibraryId: 1, Priority: 0, EnqueuedAt: T0, IgnoreMediaActivity: false),
            new QueuedJob(2, LibraryId: null, Priority: int.MaxValue, EnqueuedAt: T0, IgnoreMediaActivity: true),
        };

        var started = JobScheduler.SelectJobsToStart(
            queued,
            runningCount: 0,
            maxConcurrent: 2,
            mediaServicesActive: true);

        Assert.Equal([2], started);
    }

    [Fact]
    public void Interactive_preview_bypasses_the_library_background_window()
    {
        var preview = new QueuedJob(
            1,
            LibraryId: 2,
            Priority: int.MaxValue,
            EnqueuedAt: T0,
            IgnoreLibraryWindow: true);

        Assert.True(JobScheduler.CanRunInLibraryWindow(
            preview,
            windowStart: new TimeOnly(0, 0),
            windowEnd: new TimeOnly(9, 0),
            now: new TimeOnly(21, 0)));
    }

    [Fact]
    public void Normal_job_still_waits_for_its_closed_library_window()
    {
        var normal = new QueuedJob(1, LibraryId: 2, Priority: 0, EnqueuedAt: T0);

        Assert.False(JobScheduler.CanRunInLibraryWindow(
            normal,
            windowStart: new TimeOnly(0, 0),
            windowEnd: new TimeOnly(9, 0),
            now: new TimeOnly(21, 0)));
    }
}
