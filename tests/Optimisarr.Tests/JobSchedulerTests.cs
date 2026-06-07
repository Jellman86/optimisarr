using Optimisarr.Core.Queue;

namespace Optimisarr.Tests;

public sealed class JobSchedulerTests
{
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
}
