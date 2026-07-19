using Optimisarr.Core.Scheduling;
using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

public sealed class AutoExclusionPolicyTests
{
    [Theory]
    [InlineData(0, 3, false)]   // no failures yet
    [InlineData(2, 3, false)]   // below the threshold
    [InlineData(3, 3, true)]    // reached the threshold
    [InlineData(5, 3, true)]    // past the threshold
    public void Excludes_only_once_the_failure_threshold_is_reached(int failures, int threshold, bool expected)
    {
        Assert.Equal(expected, AutoExclusionPolicy.ShouldExclude(failures, threshold));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_threshold_of_zero_or_less_disables_auto_exclusion(int threshold)
    {
        Assert.False(AutoExclusionPolicy.ShouldExclude(100, threshold));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_size_saving_failure_is_excluded_without_another_encode(bool vmafAlsoFailed)
    {
        var checks = new List<VerificationCheck>
        {
            new("Size saving", CheckOutcome.Failed, "output was larger")
        };
        if (vmafAlsoFailed)
        {
            checks.Add(new VerificationCheck("Perceptual quality (VMAF)", CheckOutcome.Failed, "low"));
        }

        Assert.Equal(
            ImmediateAutoExclusionReason.SizeSaving,
            AutoExclusionPolicy.ImmediateReason(
                new VerificationReport(checks),
                qualityRetryCount: 0,
                effectiveQuality: 20));
    }

    [Theory]
    [InlineData(0, 20, ImmediateAutoExclusionReason.None)]
    [InlineData(1, 17, ImmediateAutoExclusionReason.VmafAfterHigherQualityRetry)]
    [InlineData(2, 14, ImmediateAutoExclusionReason.VmafAfterHigherQualityRetry)]
    [InlineData(0, 0, ImmediateAutoExclusionReason.VmafAtMaximumQuality)]
    public void An_isolated_vmaf_failure_excludes_only_when_higher_quality_recovery_is_exhausted(
        int qualityRetryCount,
        int effectiveQuality,
        ImmediateAutoExclusionReason expected)
    {
        var report = new VerificationReport(
            [new VerificationCheck("Perceptual quality (VMAF)", CheckOutcome.Failed, "low")],
            Vmaf: new VmafEvidence(true, null, new QualityScores(80, 75, 0, null, null)));

        Assert.Equal(expected, AutoExclusionPolicy.ImmediateReason(
            report,
            qualityRetryCount,
            effectiveQuality));
    }

    [Fact]
    public void Another_verification_failure_keeps_the_repeated_failure_threshold()
    {
        var report = new VerificationReport([
            new VerificationCheck("Duration", CheckOutcome.Failed, "short"),
            new VerificationCheck("Perceptual quality (VMAF)", CheckOutcome.Failed, "low")
        ]);

        Assert.Equal(
            ImmediateAutoExclusionReason.None,
            AutoExclusionPolicy.ImmediateReason(report, qualityRetryCount: 1, effectiveQuality: 17));
    }

    [Fact]
    public void An_unmeasured_vmaf_failure_is_not_immediately_excluded()
    {
        var report = new VerificationReport(
            [new VerificationCheck("Perceptual quality (VMAF)", CheckOutcome.Failed, "no comparable frames")],
            Vmaf: new VmafEvidence(false, "no comparable frames", null));

        Assert.Equal(
            ImmediateAutoExclusionReason.None,
            AutoExclusionPolicy.ImmediateReason(report, qualityRetryCount: 1, effectiveQuality: 17));
    }
}
