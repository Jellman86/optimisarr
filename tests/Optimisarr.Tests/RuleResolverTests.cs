using Optimisarr.Core.Domain;
using Optimisarr.Core.Rules;

namespace Optimisarr.Tests;

public sealed class RuleResolverTests
{
    [Fact]
    public void No_overrides_yields_the_profile_defaults()
    {
        var resolved = RuleResolver.Resolve(RuleProfile.ConservativeHevc, RuleOverrides.None);
        var defaults = RuleProfileDefaults.For(RuleProfile.ConservativeHevc);

        Assert.Equal(defaults, resolved);
    }

    [Fact]
    public void Audio_target_defaults_to_opus_and_can_be_overridden()
    {
        var defaults = RuleResolver.Resolve(RuleProfile.ConservativeHevc, RuleOverrides.None);
        Assert.Equal("opus", defaults.TargetAudioCodec);
        Assert.Equal(128, defaults.AudioBitrateKbps);

        var overridden = RuleResolver.Resolve(
            RuleProfile.ConservativeHevc,
            new RuleOverrides { TargetAudioCodec = "aac", AudioBitrateKbps = 192 });

        Assert.Equal("aac", overridden.TargetAudioCodec);
        Assert.Equal(192, overridden.AudioBitrateKbps);
    }

    [Fact]
    public void Video_audio_defaults_to_copy_and_can_be_overridden()
    {
        var defaults = RuleResolver.Resolve(RuleProfile.ConservativeHevc, RuleOverrides.None);
        // Null video-audio codec means "copy untouched"; the bitrate default is only used
        // once a codec is chosen.
        Assert.Null(defaults.VideoAudioCodec);
        Assert.Equal(160, defaults.VideoAudioBitrateKbps);

        var overridden = RuleResolver.Resolve(
            RuleProfile.ConservativeHevc,
            new RuleOverrides { VideoAudioCodec = "aac", VideoAudioBitrateKbps = 192 });

        Assert.Equal("aac", overridden.VideoAudioCodec);
        Assert.Equal(192, overridden.VideoAudioBitrateKbps);
    }

    [Fact]
    public void Downmix_to_stereo_defaults_to_off_and_can_be_overridden()
    {
        var defaults = RuleResolver.Resolve(RuleProfile.ConservativeHevc, RuleOverrides.None);
        Assert.False(defaults.DownmixToStereo);

        var overridden = RuleResolver.Resolve(
            RuleProfile.ConservativeHevc, new RuleOverrides { DownmixToStereo = true });

        Assert.True(overridden.DownmixToStereo);
    }

    [Fact]
    public void Reencode_lossy_audio_defaults_to_off_and_can_be_overridden()
    {
        var defaults = RuleResolver.Resolve(RuleProfile.ConservativeHevc, RuleOverrides.None);
        Assert.False(defaults.ReencodeLossyAudio);

        var overridden = RuleResolver.Resolve(
            RuleProfile.ConservativeHevc, new RuleOverrides { ReencodeLossyAudio = true });

        Assert.True(overridden.ReencodeLossyAudio);
    }

    [Fact]
    public void Image_target_defaults_to_webp_quality_80_and_can_be_overridden()
    {
        var defaults = RuleResolver.Resolve(RuleProfile.ConservativeHevc, RuleOverrides.None);
        Assert.Equal("webp", defaults.TargetImageFormat);
        Assert.Equal(80, defaults.ImageQuality);
        Assert.False(defaults.ReencodeLossyImages);

        var overridden = RuleResolver.Resolve(
            RuleProfile.ConservativeHevc,
            new RuleOverrides { TargetImageFormat = "webp", ImageQuality = 65, ReencodeLossyImages = true });

        Assert.Equal("webp", overridden.TargetImageFormat);
        Assert.Equal(65, overridden.ImageQuality);
        Assert.True(overridden.ReencodeLossyImages);
    }

    [Fact]
    public void Overrides_replace_only_the_values_that_are_set()
    {
        var overrides = new RuleOverrides { MaxHeight = 1080, Hdr = HdrHandling.TonemapToSdr };

        var resolved = RuleResolver.Resolve(RuleProfile.ConservativeHevc, overrides);

        Assert.Equal(1080, resolved.MaxHeight);
        Assert.Equal(HdrHandling.TonemapToSdr, resolved.Hdr);
        // Unspecified values keep the profile default.
        Assert.Equal("hevc", resolved.TargetVideoCodec);
        Assert.Equal(RuleProfileDefaults.For(RuleProfile.ConservativeHevc).MinFileSizeBytes, resolved.MinFileSizeBytes);
    }

    [Fact]
    public void Blank_container_override_falls_back_to_the_profile_default()
    {
        var overrides = new RuleOverrides { TargetContainer = "   " };

        var resolved = RuleResolver.Resolve(RuleProfile.ConservativeHevc, overrides);

        // The Conservative HEVC profile defaults to MP4 for broad device compatibility.
        Assert.Equal("mp4", resolved.TargetContainer);
    }

    [Fact]
    public void Tonemap_override_makes_hdr_content_eligible()
    {
        var rules = RuleResolver.Resolve(
            RuleProfile.ConservativeHevc,
            new RuleOverrides { Hdr = HdrHandling.TonemapToSdr });

        var hdrFile = new MediaProperties(
            "matroska,webm", "h264", 1920, 1080, 5L * 1024 * 1024 * 1024, IsHdr: true, "Film/a.mkv");

        Assert.True(CandidateEvaluator.Evaluate(hdrFile, rules).IsEligible);
    }
}
