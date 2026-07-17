using Optimisarr.Api.Queue;
using Optimisarr.Data;

namespace Optimisarr.Tests;

public sealed class DiagnosticJobRetentionTests
{
    [Theory]
    [InlineData(JobType.Calibration, JobStatus.Failed, true)]
    [InlineData(JobType.Preview, JobStatus.Failed, true)]
    [InlineData(JobType.Calibration, JobStatus.Completed, false)]
    [InlineData(JobType.Preview, JobStatus.Cancelled, false)]
    [InlineData(JobType.Normal, JobStatus.Failed, false)]
    public void Only_failed_disposable_jobs_are_retained_for_diagnostics(
        JobType type,
        JobStatus status,
        bool expected)
    {
        Assert.Equal(expected, DiagnosticJobRetention.ShouldRetain(type, status));
    }
}
