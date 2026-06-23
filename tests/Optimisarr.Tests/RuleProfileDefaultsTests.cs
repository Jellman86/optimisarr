using Optimisarr.Core.Domain;
using Optimisarr.Core.Rules;

namespace Optimisarr.Tests;

public sealed class RuleProfileDefaultsTests
{
    [Theory]
    [InlineData(RuleProfile.ConservativeHevc, "hevc")]
    [InlineData(RuleProfile.CompatibilityH264, "h264")]
    [InlineData(RuleProfile.ExperimentalAv1, "av1")]
    [InlineData(RuleProfile.ScottsSettings, "hevc")]
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
    [InlineData(RuleProfile.ScottsSettings, "mp4")]
    public void Profiles_target_the_researched_container(RuleProfile profile, string expectedContainer)
    {
        var settings = RuleProfileDefaults.For(profile);

        Assert.Equal(expectedContainer, settings.TargetContainer);
    }

    [Theory]
    [InlineData(RuleProfile.ConservativeHevc, 24)]
    [InlineData(RuleProfile.CompatibilityH264, 20)]
    [InlineData(RuleProfile.ExperimentalAv1, 30)]
    [InlineData(RuleProfile.ScottsSettings, 24)]
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
    public void Scotts_settings_bundles_tonemap_and_aac_96kbps_stereo_downmix()
    {
        var settings = RuleProfileDefaults.For(RuleProfile.ScottsSettings);

        // HDR is tone-mapped to SDR for compatibility.
        Assert.Equal(HdrHandling.TonemapToSdr, settings.Hdr);
        // A video job re-encodes its audio to AAC 96 kbps, downmixed to stereo.
        Assert.Equal("aac", settings.VideoAudioCodec);
        Assert.Equal(96, settings.VideoAudioBitrateKbps);
        Assert.True(settings.DownmixToStereo);
        // A music/audio-only library on this profile gets the same AAC 96 kbps target.
        Assert.Equal("aac", settings.TargetAudioCodec);
        Assert.Equal(96, settings.AudioBitrateKbps);
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
