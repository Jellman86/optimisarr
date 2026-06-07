using Optimisarr.Core.Domain;
using Optimisarr.Core.Rules;

namespace Optimisarr.Tests;

public sealed class RuleProfileDefaultsTests
{
    [Theory]
    [InlineData(RuleProfile.ConservativeHevc, "hevc")]
    [InlineData(RuleProfile.CompatibilityH264, "h264")]
    [InlineData(RuleProfile.ExperimentalAv1, "av1")]
    public void Reencode_profiles_target_the_expected_codec(RuleProfile profile, string expectedCodec)
    {
        var settings = RuleProfileDefaults.For(profile);

        Assert.Equal(expectedCodec, settings.TargetVideoCodec);
    }

    [Fact]
    public void Remux_profile_has_no_target_codec()
    {
        var settings = RuleProfileDefaults.For(RuleProfile.RemuxCleanup);

        Assert.Null(settings.TargetVideoCodec);
    }

    [Fact]
    public void Conservative_and_compatibility_profiles_exclude_hdr_by_default()
    {
        Assert.True(RuleProfileDefaults.For(RuleProfile.ConservativeHevc).ExcludeHdr);
        Assert.True(RuleProfileDefaults.For(RuleProfile.CompatibilityH264).ExcludeHdr);
    }

    [Fact]
    public void Every_profile_has_defaults()
    {
        foreach (var profile in Enum.GetValues<RuleProfile>())
        {
            var settings = RuleProfileDefaults.For(profile);
            Assert.Equal(profile, settings.Profile);
        }
    }
}
