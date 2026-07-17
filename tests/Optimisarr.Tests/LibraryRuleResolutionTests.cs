using Optimisarr.Api.Library;
using Optimisarr.Core.Domain;
using Optimisarr.Data;

namespace Optimisarr.Tests;

public sealed class LibraryRuleResolutionTests
{
    [Fact]
    public void A_library_keeps_the_profile_efficiency_floor_by_default()
    {
        var library = new Library { Name = "Films", Path = "/data/films", RuleProfile = RuleProfile.ConservativeHevc };

        var rules = LibraryRuleResolution.Resolve(library);

        Assert.True(library.SkipEfficientSources);                         // default on
        Assert.NotNull(rules.MinSourceBitsPerPixelSecond);                 // the profile's floor applies
    }

    [Fact]
    public void Turning_off_skip_efficient_sources_disables_the_floor_for_that_library()
    {
        var library = new Library
        {
            Name = "Films", Path = "/data/films", RuleProfile = RuleProfile.ConservativeHevc,
            SkipEfficientSources = false
        };

        var rules = LibraryRuleResolution.Resolve(library);

        Assert.Null(rules.MinSourceBitsPerPixelSecond);   // floor removed, so every source reaches the encoder
    }

    [Fact]
    public void Stored_kept_audio_languages_resolve_into_the_rules()
    {
        var library = new Library
        {
            Name = "Films", Path = "/data/films", RuleProfile = RuleProfile.ConservativeHevc,
            KeepAudioLanguages = "eng, jpn"
        };

        var rules = LibraryRuleResolution.Resolve(library);

        Assert.Equal(new[] { "eng", "jpn" }, rules.KeepAudioLanguages);
    }

    [Fact]
    public void An_unset_kept_audio_languages_column_keeps_every_track()
    {
        var library = new Library { Name = "Films", Path = "/data/films", RuleProfile = RuleProfile.ConservativeHevc };

        var rules = LibraryRuleResolution.Resolve(library);

        Assert.Empty(rules.KeepAudioLanguages);
    }

    [Fact]
    public void Calibration_slider_preset_replaces_stale_video_codec_and_container_overrides()
    {
        var library = new Library
        {
            Name = "Films",
            Path = "/data/films",
            RuleProfile = RuleProfile.ConservativeHevc,
            TargetVideoCodec = "hevc",
            TargetContainer = "mp4"
        };

        var rules = LibraryRuleResolution.ResolveVideoPreset(library, RuleProfile.ExperimentalAv1);

        Assert.Equal("av1", rules.TargetVideoCodec);
        Assert.Equal("mkv", rules.TargetContainer);
        Assert.Equal(30, rules.DefaultCrf);
    }

    [Fact]
    public void Scotts_calibration_preset_resolves_the_same_complete_bundle_as_the_slider()
    {
        var library = new Library
        {
            Name = "Films",
            Path = "/data/films",
            RuleProfile = RuleProfile.CompatibilityH264,
            VideoAudioCodec = "copy",
            DownmixToStereo = false
        };

        var rules = LibraryRuleResolution.ResolveVideoPreset(library, RuleProfile.ScottsSettings);

        Assert.Equal("hevc", rules.TargetVideoCodec);
        Assert.Equal("mp4", rules.TargetContainer);
        Assert.Equal("aac", rules.VideoAudioCodec);
        Assert.Equal(96, rules.VideoAudioBitrateKbps);
        Assert.True(rules.DownmixToStereo);
        Assert.Equal(HdrHandling.Preserve, rules.Hdr);
    }
}
