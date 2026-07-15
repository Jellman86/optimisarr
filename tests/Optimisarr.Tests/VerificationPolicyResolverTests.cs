using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

public sealed class VerificationPolicyResolverTests
{
    private static readonly VerificationPolicy GateOn =
        VerificationPolicy.Default with { QualityGateEnabled = true, MinimumVmafHarmonicMean = 93, MinimumVmafMin = 80 };

    [Fact]
    public void No_overrides_returns_the_global_policy()
    {
        var resolved = VerificationPolicyResolver.Resolve(GateOn, Overrides());

        Assert.Equal(93, resolved.MinimumVmafHarmonicMean);
        Assert.Equal(80, resolved.MinimumVmafMin);
    }

    [Fact]
    public void An_archive_library_can_demand_higher_quality()
    {
        var resolved = VerificationPolicyResolver.Resolve(GateOn, Overrides(harmonic: 97, fifth: 90));

        Assert.Equal(97, resolved.MinimumVmafHarmonicMean);
        Assert.Equal(90, resolved.MinimumVmafMin);
    }

    [Fact]
    public void A_single_override_leaves_the_other_at_the_global_value()
    {
        var resolved = VerificationPolicyResolver.Resolve(GateOn, Overrides(harmonic: 96));

        Assert.Equal(96, resolved.MinimumVmafHarmonicMean);
        Assert.Equal(80, resolved.MinimumVmafMin);
    }

    [Fact]
    public void A_library_can_enable_the_gate_when_the_global_default_is_off()
    {
        var disabled = VerificationPolicy.Default with { QualityGateEnabled = false };

        var resolved = VerificationPolicyResolver.Resolve(disabled, Overrides(enabled: true, harmonic: 90));

        Assert.True(resolved.QualityGateEnabled);
        Assert.Equal(90, resolved.MinimumVmafHarmonicMean);
    }

    [Fact]
    public void A_library_can_disable_the_gate_when_the_global_default_is_on()
    {
        var resolved = VerificationPolicyResolver.Resolve(GateOn, Overrides(enabled: false));

        Assert.False(resolved.QualityGateEnabled);
    }

    [Fact]
    public void Overrides_are_clamped_to_the_valid_vmaf_range()
    {
        var resolved = VerificationPolicyResolver.Resolve(GateOn, Overrides(
            harmonic: 150,
            fifth: -5,
            catastrophic: 120,
            clip: true,
            frameSubsample: 99));

        Assert.Equal(100, resolved.MinimumVmafHarmonicMean);
        Assert.Equal(0, resolved.MinimumVmafMin);
        Assert.Equal(100, resolved.MinimumVmafCatastrophicMin);
        Assert.True(resolved.ClipVmafEnabled);
        Assert.Equal(10, resolved.VmafFrameSubsample);
    }

    private static VerificationPolicyOverrides Overrides(
        bool? enabled = null,
        double? harmonic = null,
        double? fifth = null,
        double? catastrophic = null,
        bool? clip = null,
        int? frameSubsample = null) =>
        new(enabled, harmonic, fifth, catastrophic, clip, frameSubsample);
}
