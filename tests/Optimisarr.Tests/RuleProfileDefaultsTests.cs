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

    [Theory]
    [InlineData(RuleProfile.ConservativeHevc, "mp4")]
    [InlineData(RuleProfile.CompatibilityH264, "mp4")]
    [InlineData(RuleProfile.ExperimentalAv1, "mkv")]
    [InlineData(RuleProfile.RemuxCleanup, "mkv")]
    public void Profiles_target_the_researched_container(RuleProfile profile, string expectedContainer)
    {
        var settings = RuleProfileDefaults.For(profile);

        Assert.Equal(expectedContainer, settings.TargetContainer);
    }

    [Theory]
    [InlineData(RuleProfile.ConservativeHevc, 24)]
    [InlineData(RuleProfile.CompatibilityH264, 20)]
    [InlineData(RuleProfile.ExperimentalAv1, 30)]
    public void Reencode_profiles_default_to_a_transparent_crf(RuleProfile profile, int expectedCrf)
    {
        var settings = RuleProfileDefaults.For(profile);

        Assert.Equal(expectedCrf, settings.DefaultCrf);
    }

    [Fact]
    public void Remux_profile_has_no_default_crf()
    {
        Assert.Null(RuleProfileDefaults.For(RuleProfile.RemuxCleanup).DefaultCrf);
    }

    [Fact]
    public void Conservative_and_compatibility_profiles_exclude_hdr_by_default()
    {
        Assert.Equal(HdrHandling.Exclude, RuleProfileDefaults.For(RuleProfile.ConservativeHevc).Hdr);
        Assert.Equal(HdrHandling.Exclude, RuleProfileDefaults.For(RuleProfile.CompatibilityH264).Hdr);
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
