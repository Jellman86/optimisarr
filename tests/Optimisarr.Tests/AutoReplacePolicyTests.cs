using Optimisarr.Api.Queue;
using Optimisarr.Data;

namespace Optimisarr.Tests;

public sealed class AutoReplacePolicyTests
{
    [Fact]
    public void Reconciles_a_verified_ready_job_when_the_library_auto_replaces()
    {
        Assert.True(AutoReplacePolicy.ShouldReconcile(JobStatus.ReadyToReplace, verificationPassed: true, libraryAutoReplace: true));
    }

    [Fact]
    public void Skips_when_the_library_does_not_auto_replace()
    {
        Assert.False(AutoReplacePolicy.ShouldReconcile(JobStatus.ReadyToReplace, verificationPassed: true, libraryAutoReplace: false));
    }

    [Theory]
    [InlineData(JobStatus.Queued)]
    [InlineData(JobStatus.Transcoding)]
    [InlineData(JobStatus.Verifying)]
    [InlineData(JobStatus.Completed)]
    [InlineData(JobStatus.Failed)]
    public void Only_ready_to_replace_jobs_are_reconciled(JobStatus status)
    {
        Assert.False(AutoReplacePolicy.ShouldReconcile(status, verificationPassed: true, libraryAutoReplace: true));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public void Never_replaces_a_job_that_did_not_pass_verification(bool? verificationPassed)
    {
        Assert.False(AutoReplacePolicy.ShouldReconcile(JobStatus.ReadyToReplace, verificationPassed, libraryAutoReplace: true));
    }
}
