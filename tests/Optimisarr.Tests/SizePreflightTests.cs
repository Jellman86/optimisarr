using Optimisarr.Core.Queue;

namespace Optimisarr.Tests;

public sealed class SizePreflightTests
{
    [Fact]
    public void Strong_sample_growth_holds_before_full_encode()
    {
        var result = SizePreflight.Assess(
            sourceBytes: 10_000_000_000,
            sourceDurationSeconds: 3_600,
            sampledDurationSeconds: 120,
            encodedSampleBytes: 500_000_000,
            selectedProbePassedQuality: true,
            requireSizeReduction: true,
            minimumSavingPercent: null,
            bypass: false);

        Assert.True(result.ShouldHold);
        Assert.Equal(15_000_000_000, result.ProjectedVideoBytes);
        Assert.Equal(50, result.ProjectedPercentChange);
    }

    [Theory]
    [InlineData(350_000_000, false)] // only 5% growth is too uncertain to hold
    [InlineData(410_000_000, false)] // below the conservative 25% margin
    [InlineData(500_000_000, true)]
    public void Projection_requires_a_clear_margin(long sampleBytes, bool hold)
    {
        var result = SizePreflight.Assess(10_000_000_000, 3_600, 120, sampleBytes,
            selectedProbePassedQuality: true, requireSizeReduction: true,
            minimumSavingPercent: null, bypass: false);

        Assert.Equal(hold, result.ShouldHold);
    }

    [Theory]
    [InlineData(false, true, false)] // quality was not proved
    [InlineData(true, false, false)] // library does not require a saving
    [InlineData(true, true, true)]
    public void Only_proven_quality_under_a_size_gate_can_hold(
        bool passedQuality, bool requireSaving, bool expected)
    {
        var result = SizePreflight.Assess(10_000_000_000, 3_600, 120, 500_000_000,
            passedQuality, requireSaving, null, bypass: false);

        Assert.Equal(expected, result.ShouldHold);
    }

    [Fact]
    public void Explicit_approval_bypasses_the_estimate_without_weakening_final_gate()
    {
        var result = SizePreflight.Assess(10_000_000_000, 3_600, 120, 500_000_000,
            selectedProbePassedQuality: true, requireSizeReduction: true,
            minimumSavingPercent: null, bypass: true);

        Assert.False(result.ShouldHold);
    }

    [Theory]
    [InlineData(0, 3_600, 120)]
    [InlineData(10_000_000_000, 0, 120)]
    [InlineData(10_000_000_000, 3_600, 0)]
    [InlineData(10_000_000_000, 120, 120)]
    public void Incomplete_or_unrepresentative_evidence_never_holds(
        long sourceBytes, double duration, double sampledSeconds)
    {
        var result = SizePreflight.Assess(sourceBytes, duration, sampledSeconds, 500_000_000,
            selectedProbePassedQuality: true, requireSizeReduction: true,
            minimumSavingPercent: null, bypass: false);

        Assert.False(result.ShouldHold);
    }

    [Fact]
    public void Minimum_saving_target_is_used_for_the_estimate()
    {
        var result = SizePreflight.Assess(10_000_000_000, 3_600, 120, 400_000_000,
            selectedProbePassedQuality: true, requireSizeReduction: true,
            minimumSavingPercent: 10, bypass: false);

        // 12 GB projected video alone is 33% above the 9 GB allowed candidate.
        Assert.True(result.ShouldHold);
    }
}
