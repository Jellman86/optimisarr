using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

public sealed class VmafRetryPolicyTests
{
    [Fact]
    public void An_isolated_vmaf_rejection_gets_one_higher_quality_retry()
    {
        var report = new VerificationReport([
            new VerificationCheck("Decode health", CheckOutcome.Passed, "ok"),
            new VerificationCheck("Perceptual quality (VMAF)", CheckOutcome.Failed, "low")
        ]);

        Assert.True(VmafRetryPolicy.ShouldRetry(report, retryCount: 0, effectiveQuality: 20));
        Assert.False(VmafRetryPolicy.ShouldRetry(report, retryCount: 1, effectiveQuality: 17));
    }

    [Fact]
    public void Another_failed_safety_gate_prevents_an_automatic_quality_retry()
    {
        var report = new VerificationReport([
            new VerificationCheck("Duration", CheckOutcome.Failed, "short"),
            new VerificationCheck("Perceptual quality (VMAF)", CheckOutcome.Failed, "low")
        ]);

        Assert.False(VmafRetryPolicy.ShouldRetry(report, retryCount: 0, effectiveQuality: 20));
    }
}
