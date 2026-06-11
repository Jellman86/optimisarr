using Optimisarr.Api.Queue;
using Optimisarr.Data;

namespace Optimisarr.Tests;

public sealed class JobClearingTests
{
    private static readonly HashSet<int> NoLiveRollbacks = new();

    private static Job Job(int id, JobStatus status) => new() { Id = id, Status = status };

    [Theory]
    [InlineData(JobStatus.Completed)]
    [InlineData(JobStatus.Failed)]
    [InlineData(JobStatus.Cancelled)]
    public void Terminal_jobs_without_a_live_rollback_are_clearable(JobStatus status)
    {
        Assert.True(JobClearing.IsClearable(Job(1, status), NoLiveRollbacks));
    }

    [Theory]
    [InlineData(JobStatus.Queued)]
    [InlineData(JobStatus.Probing)]
    [InlineData(JobStatus.Transcoding)]
    [InlineData(JobStatus.Verifying)]
    [InlineData(JobStatus.ReadyToReplace)]
    public void In_flight_jobs_are_never_clearable(JobStatus status)
    {
        Assert.False(JobClearing.IsClearable(Job(1, status), NoLiveRollbacks));
    }

    [Fact]
    public void A_completed_job_with_a_live_rollback_is_protected()
    {
        var liveRollbacks = new HashSet<int> { 7 };

        Assert.False(JobClearing.IsClearable(Job(7, JobStatus.Completed), liveRollbacks));
    }

    [Fact]
    public void A_completed_job_whose_rollback_is_spent_is_clearable()
    {
        // Job 7 has no entry in the live-rollback set (its replacement was rolled back or purged).
        var liveRollbacks = new HashSet<int> { 99 };

        Assert.True(JobClearing.IsClearable(Job(7, JobStatus.Completed), liveRollbacks));
    }
}
