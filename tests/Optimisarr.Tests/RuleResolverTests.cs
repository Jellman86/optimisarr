using Optimisarr.Core.Domain;
using Optimisarr.Core.Queue;
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
    public void Audio_target_defaults_to_aac_and_can_be_overridden()
    {
        var defaults = RuleResolver.Resolve(RuleProfile.ConservativeHevc, RuleOverrides.None);
        Assert.Equal("aac", defaults.TargetAudioCodec);
        Assert.Equal(128, defaults.AudioBitrateKbps);

        var overridden = RuleResolver.Resolve(
            RuleProfile.ConservativeHevc,
            new RuleOverrides { TargetAudioCodec = "opus", AudioBitrateKbps = 192 });

        Assert.Equal("opus", overridden.TargetAudioCodec);
        Assert.Equal(192, overridden.AudioBitrateKbps);
    }

    [Fact]
    public void Compatibility_video_audio_defaults_to_aac_and_can_be_explicitly_copied()
    {
        var defaults = RuleResolver.Resolve(RuleProfile.ConservativeHevc, RuleOverrides.None);
        Assert.Equal("aac", defaults.VideoAudioCodec);
        Assert.Equal(160, defaults.VideoAudioBitrateKbps);

        var copied = RuleResolver.Resolve(
            RuleProfile.ConservativeHevc,
            new RuleOverrides { VideoAudioCodec = "copy" });

        Assert.Null(copied.VideoAudioCodec);

        var overridden = RuleResolver.Resolve(
            RuleProfile.ConservativeHevc,
            new RuleOverrides { VideoAudioCodec = "aac", VideoAudioBitrateKbps = 192 });
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
    public void Image_target_defaults_to_jpeg_quality_80_and_can_be_overridden()
    {
        var defaults = RuleResolver.Resolve(RuleProfile.ConservativeHevc, RuleOverrides.None);
        Assert.Equal("jpeg", defaults.TargetImageFormat);
        Assert.Equal(80, defaults.ImageQuality);
        Assert.False(defaults.ReencodeLossyImages);
        Assert.Equal(ImageDownscaleMode.None, defaults.ImageDownscaleMode);

        var overridden = RuleResolver.Resolve(
            RuleProfile.ConservativeHevc,
            new RuleOverrides
            {
                TargetImageFormat = "avif",
                ImageQuality = 65,
                ReencodeLossyImages = true,
                ImageDownscaleMode = ImageDownscaleMode.MaxLongEdge,
                ImageDownscaleValue = 1920
            });

        Assert.Equal("avif", overridden.TargetImageFormat);
        Assert.Equal(65, overridden.ImageQuality);
        Assert.True(overridden.ReencodeLossyImages);
        Assert.Equal(ImageDownscaleMode.MaxLongEdge, overridden.ImageDownscaleMode);
        Assert.Equal(1920, overridden.ImageDownscaleValue);
    }

    [Fact]
    public void Kept_audio_languages_default_to_empty_and_can_be_overridden()
    {
        var defaults = RuleResolver.Resolve(RuleProfile.ConservativeHevc, RuleOverrides.None);
        Assert.Empty(defaults.KeepAudioLanguages);

        var overridden = RuleResolver.Resolve(
            RuleProfile.ConservativeHevc,
            new RuleOverrides { KeepAudioLanguages = new[] { "eng", "jpn" } });

        Assert.Equal(new[] { "eng", "jpn" }, overridden.KeepAudioLanguages);
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
