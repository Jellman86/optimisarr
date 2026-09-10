using Optimisarr.Api.Queue;
using Optimisarr.Api.Replacement;
using Optimisarr.Data;

namespace Optimisarr.Tests;

public sealed class AutoReplacePolicyTests
{
    [Fact]
    public void Reconciles_a_verified_ready_job_when_the_library_auto_replaces()
    {
        Assert.True(AutoReplacePolicy.ShouldReconcile(
            JobStatus.ReadyToReplace,
            verificationPassed: true,
            libraryAutoReplace: true,
            dryRunMode: false));
    }

    [Fact]
    public void Skips_when_the_library_does_not_auto_replace()
    {
        Assert.False(AutoReplacePolicy.ShouldReconcile(
            JobStatus.ReadyToReplace,
            verificationPassed: true,
            libraryAutoReplace: false,
            dryRunMode: false));
    }

    [Fact]
    public void Skips_while_dry_run_mode_is_enabled()
    {
        Assert.False(AutoReplacePolicy.ShouldReconcile(
            JobStatus.ReadyToReplace,
            verificationPassed: true,
            libraryAutoReplace: true,
            dryRunMode: true));
    }

    [Fact]
    public void Skips_while_the_operator_has_paused_the_queue()
    {
        Assert.False(AutoReplacePolicy.ShouldReconcile(
            JobStatus.ReadyToReplace,
            verificationPassed: true,
            libraryAutoReplace: true,
            dryRunMode: false,
            manuallyPaused: true));
    }

    [Theory]
    [InlineData(JobStatus.Queued)]
    [InlineData(JobStatus.Transcoding)]
    [InlineData(JobStatus.Verifying)]
    [InlineData(JobStatus.Completed)]
    [InlineData(JobStatus.Failed)]
    public void Only_ready_to_replace_jobs_are_reconciled(JobStatus status)
    {
        Assert.False(AutoReplacePolicy.ShouldReconcile(
            status,
            verificationPassed: true,
            libraryAutoReplace: true,
            dryRunMode: false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public void Never_replaces_a_job_that_did_not_pass_verification(bool? verificationPassed)
    {
        Assert.False(AutoReplacePolicy.ShouldReconcile(
            JobStatus.ReadyToReplace,
            verificationPassed,
            libraryAutoReplace: true,
            dryRunMode: false));
    }

    [Fact]
    public void A_replacement_a_library_rule_declined_is_not_reported_as_a_fault()
    {
        // Reconciliation polls every three seconds. A file that stays hardlinked — someone seeding
        // a torrent for a fortnight — would otherwise log a warning on every pass, forever, which
        // is exactly the "bury real warnings" failure the permanent-failure branch exists to avoid.
        Assert.False(AutoReplacePolicy.IsFault(ReplacementResultKind.Deferred));
    }

    [Fact]
    public void A_declined_replacement_is_still_not_treated_as_success()
    {
        // Quiet must not mean invisible in the wrong direction: nothing was replaced, so the job
        // stays ReadyToReplace and can complete later once the other link is gone.
        Assert.NotEqual(ReplacementResultKind.Success, ReplacementResultKind.Deferred);
    }

    [Fact]
    public void Genuine_problems_are_still_reported_as_faults()
    {
        Assert.True(AutoReplacePolicy.IsFault(ReplacementResultKind.Failed));
        Assert.True(AutoReplacePolicy.IsFault(ReplacementResultKind.Invalid));
        Assert.True(AutoReplacePolicy.IsFault(ReplacementResultKind.NotFound));
    }
}
