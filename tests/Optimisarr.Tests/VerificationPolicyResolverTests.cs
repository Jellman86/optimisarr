using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

public sealed class VerificationPolicyResolverTests
{
    private static readonly VerificationPolicy GateOn =
        VerificationPolicy.Default with { QualityGateEnabled = true, MinimumVmafHarmonicMean = 93, MinimumVmafMin = 80 };

    [Fact]
    public void No_overrides_returns_the_global_policy()
    {
        var resolved = VerificationPolicyResolver.Resolve(GateOn, null, null);

        Assert.Equal(93, resolved.MinimumVmafHarmonicMean);
        Assert.Equal(80, resolved.MinimumVmafMin);
    }

    [Fact]
    public void An_archive_library_can_demand_higher_quality()
    {
        var resolved = VerificationPolicyResolver.Resolve(GateOn, 97, 90);

        Assert.Equal(97, resolved.MinimumVmafHarmonicMean);
        Assert.Equal(90, resolved.MinimumVmafMin);
    }

    [Fact]
    public void A_single_override_leaves_the_other_at_the_global_value()
    {
        var resolved = VerificationPolicyResolver.Resolve(GateOn, 96, null);

        Assert.Equal(96, resolved.MinimumVmafHarmonicMean);
        Assert.Equal(80, resolved.MinimumVmafMin);
    }

    [Fact]
    public void Overrides_are_ignored_when_the_gate_is_disabled()
    {
        var resolved = VerificationPolicyResolver.Resolve(VerificationPolicy.Default, 97, 90);

        Assert.Same(VerificationPolicy.Default, resolved);
    }

    [Fact]
    public void Overrides_are_clamped_to_the_valid_vmaf_range()
    {
        var resolved = VerificationPolicyResolver.Resolve(GateOn, 150, -5);

        Assert.Equal(100, resolved.MinimumVmafHarmonicMean);
        Assert.Equal(0, resolved.MinimumVmafMin);
    }
}
