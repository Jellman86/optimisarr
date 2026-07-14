using Optimisarr.Core.Domain;
using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

public sealed class VerificationPolicyTests
{
    [Fact]
    public void Vmaf_gate_is_off_by_default()
    {
        Assert.False(VerificationPolicy.Default.QualityGateEnabled);
        Assert.False(VerificationPolicy.Default.RequiresVmaf(MediaKind.Video, videoReencoded: true));
    }

    [Fact]
    public void Explicit_opt_in_requires_vmaf_for_video_reencodes()
    {
        var policy = VerificationPolicy.Default with { QualityGateEnabled = true };

        Assert.True(policy.RequiresVmaf(MediaKind.Video, videoReencoded: true));
    }

    [Theory]
    [InlineData(MediaKind.Video, false)]
    [InlineData(MediaKind.Audio, true)]
    [InlineData(MediaKind.Image, true)]
    [InlineData(MediaKind.Unknown, true)]
    public void Vmaf_is_not_measured_for_non_video_reencodes(MediaKind kind, bool videoReencoded)
    {
        Assert.False(VerificationPolicy.Default.RequiresVmaf(kind, videoReencoded));
    }

    [Fact]
    public void Explicit_opt_out_disables_vmaf_for_video_reencodes()
    {
        var policy = VerificationPolicy.Default with { QualityGateEnabled = false };

        Assert.False(policy.RequiresVmaf(MediaKind.Video, videoReencoded: true));
    }
}
