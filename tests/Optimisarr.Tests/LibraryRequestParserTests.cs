using Optimisarr.Api.Library;
using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

public sealed class LibraryRequestParserTests
{
    // A baseline valid request; the path must exist because the parser verifies it.
    private static SaveLibraryRequest Request(
        string? keepAudioLanguages = null,
        string? keepSubtitleLanguages = null,
        string? encoderPreset = null) => new(
        Name: "Films",
        Path: Path.GetTempPath(),
        MediaType: "Film",
        RuleProfile: "ConservativeHevc",
        Enabled: true,
        Priority: 0,
        MinFileSizeBytes: null,
        MaxHeight: null,
        VideoDownscaleHeight: null,
        CropBlackBars: null,
        ReencodeSameCodecAboveBytes: null,
        SkipEfficientSources: null,
        TargetVideoCodec: null,
        TargetContainer: null,
        HdrHandling: null,
        OptimiseDolbyVision: null,
        ExcludePaths: null,
        ExcludeHardLinkedFiles: null,
        SkipSourceCodecs: null,
        ContentTune: null,
        MaxBitrateKbps: null,
        MinBitrateKbps: null,
        StrongerAdaptiveQuantisation: null,
        QualityCrf: null,
        EncoderPreset: encoderPreset,
        AudioTargetCodec: null,
        AudioBitrateKbps: null,
        VideoAudioCodec: null,
        VideoAudioBitrateKbps: null,
        DownmixToStereo: null,
        KeepAudioLanguages: keepAudioLanguages,
        KeepSubtitleLanguages: keepSubtitleLanguages,
        ReencodeLossyAudio: null,
        TargetImageFormat: null,
        ImageQuality: null,
        ReencodeLossyImages: null,
        ImageDownscaleMode: null,
        ImageDownscaleValue: null,
        MoveOnComplete: null,
        TargetFolder: null,
        MoveOverwrite: null,
        MinVmafHarmonicMean: null,
        MinVmafMin: null,
        VmafQualityGateEnabled: null,
        MinVmafCatastrophicMin: null,
        ClipVmafEnabled: null,
        VmafFrameSubsample: null,
        AutoEnqueueEnabled: null,
        AutoEnqueueWindowStart: null,
        AutoEnqueueWindowEnd: null,
        AutoReplace: null,
        VideoQualityStrategy: null);

    [Theory]
    [InlineData("5")]
    [InlineData("999")]
    [InlineData("-1")]
    public void A_numeric_content_tune_is_refused_rather_than_becoming_an_undefined_value(string tune)
    {
        // Enum.TryParse accepts a number for any enum and hands back whatever integer it was given,
        // defined or not. Left unchecked, "999" would store a ContentTune that is not a member —
        // and the tuning policy, asking only "is this Animation?", would silently encode it as
        // grain. The contract is names, so only names are accepted.
        var ok = LibraryRequestParser.TryParse(
            Request() with { ContentTune = tune }, out _, out var error);

        Assert.False(ok);
        Assert.Contains("content tune", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Animation", Optimisarr.Core.Queue.ContentTune.Animation)]
    [InlineData("animation", Optimisarr.Core.Queue.ContentTune.Animation)]
    [InlineData("Grain", Optimisarr.Core.Queue.ContentTune.Grain)]
    [InlineData("None", Optimisarr.Core.Queue.ContentTune.None)]
    public void A_named_content_tune_is_accepted_whatever_its_casing(
        string tune, Optimisarr.Core.Queue.ContentTune expected)
    {
        var ok = LibraryRequestParser.TryParse(
            Request() with { ContentTune = tune }, out var parsed, out var error);

        Assert.True(ok, error);
        Assert.Equal(expected, parsed.ContentTune);
    }

    [Fact]
    public void A_bitrate_floor_above_the_cap_is_refused()
    {
        // An inverted window is impossible to honour. Refused at the boundary so an operator sees
        // the mistake, rather than stored and then silently dropped at encode time.
        var ok = LibraryRequestParser.TryParse(
            Request() with { MaxBitrateKbps = 4000, MinBitrateKbps = 6000 }, out _, out var error);

        Assert.False(ok);
        Assert.Contains("cannot be above", error);
    }

    [Fact]
    public void A_bitrate_floor_without_a_cap_is_refused()
    {
        // A floor is a VBV constraint. Without the ceiling that defines the window there is nothing
        // for it to mean, and x264/x265 would ignore it — a setting that looks applied and is not.
        var ok = LibraryRequestParser.TryParse(
            Request() with { MinBitrateKbps = 2000 }, out _, out var error);

        Assert.False(ok);
        Assert.Contains("needs a maximum", error);
    }

    [Fact]
    public void A_bitrate_floor_inside_the_window_is_accepted()
    {
        var ok = LibraryRequestParser.TryParse(
            Request() with { MaxBitrateKbps = 8000, MinBitrateKbps = 2000 }, out var parsed, out var error);

        Assert.True(ok, error);
        Assert.Equal(2000, parsed.MinBitrateKbps);
        Assert.Equal(8000, parsed.MaxBitrateKbps);
    }

    [Fact]
    public void A_bitrate_floor_equal_to_the_cap_is_accepted_as_a_fixed_rate()
    {
        // min == max is the conventional way to ask for constant bitrate; it is a valid window.
        var ok = LibraryRequestParser.TryParse(
            Request() with { MaxBitrateKbps = 5000, MinBitrateKbps = 5000 }, out _, out var error);

        Assert.True(ok, error);
    }

    [Theory]
    [InlineData(719)]
    [InlineData(100)]
    [InlineData(5000)]
    public void An_odd_or_out_of_range_downscale_height_is_refused(int height)
    {
        // Odd because 4:2:0 chroma needs even dimensions — an odd height is a scale filter that
        // fails, not a picture that is slightly the wrong size. Bounded to real display heights.
        var ok = LibraryRequestParser.TryParse(
            Request() with { VideoDownscaleHeight = height }, out _, out var error);

        Assert.False(ok);
        Assert.Contains("Downscale height", error);
    }

    [Theory]
    [InlineData(2160)]
    [InlineData(1080)]
    [InlineData(720)]
    [InlineData(480)]
    public void A_standard_downscale_height_is_accepted(int height)
    {
        var ok = LibraryRequestParser.TryParse(
            Request() with { VideoDownscaleHeight = height }, out var parsed, out var error);

        Assert.True(ok, error);
        Assert.Equal(height, parsed.VideoDownscaleHeight);
    }

    [Fact]
    public void An_omitted_content_tune_means_no_tune_rather_than_an_error()
    {
        var ok = LibraryRequestParser.TryParse(Request(), out var parsed, out var error);

        Assert.True(ok, error);
        Assert.Equal(Optimisarr.Core.Queue.ContentTune.None, parsed.ContentTune);
    }

    [Fact]
    public void An_absurdly_long_codec_exclusion_list_is_refused()
    {
        // The form offers a fixed set of chips, but the API takes free text and this is persisted.
        // Bounding it keeps a malformed or hostile request from writing an unbounded column.
        var ok = LibraryRequestParser.TryParse(
            Request() with { SkipSourceCodecs = string.Join(",", Enumerable.Repeat("av1", 500)) },
            out _,
            out var error);

        Assert.False(ok);
        Assert.Contains("codec", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Omitted_quality_strategy_defaults_to_adaptive_vmaf_with_a_concrete_target()
    {
        var ok = LibraryRequestParser.TryParse(Request(), out var parsed, out var error);

        Assert.True(ok, error);
        Assert.Equal(Optimisarr.Core.Queue.VideoQualityStrategy.AdaptiveVmaf, parsed.VideoQualityStrategy);
        Assert.True(parsed.VmafQualityGateEnabled);
        Assert.Equal(93, parsed.MinVmafHarmonicMean);
        Assert.Equal(80, parsed.MinVmafMin);
        Assert.Equal(50, parsed.MinVmafCatastrophicMin);
        Assert.True(parsed.ClipVmafEnabled);
        Assert.Equal(1, parsed.VmafFrameSubsample);
    }

    [Theory]
    [InlineData("Music", "ConservativeHevc")]
    [InlineData("Photo", "ConservativeHevc")]
    [InlineData("Film", "RemuxCleanup")]
    public void Omitted_quality_strategy_stays_fixed_when_the_library_does_not_reencode_video(
        string mediaType,
        string ruleProfile)
    {
        var request = Request() with { MediaType = mediaType, RuleProfile = ruleProfile };

        var ok = LibraryRequestParser.TryParse(request, out var parsed, out var error);

        Assert.True(ok, error);
        Assert.Equal(Optimisarr.Core.Queue.VideoQualityStrategy.Fixed, parsed.VideoQualityStrategy);
    }

    [Fact]
    public void Explicitly_disabled_vmaf_keeps_the_fixed_path_when_the_strategy_is_omitted()
    {
        var request = Request() with { VmafQualityGateEnabled = false };

        var ok = LibraryRequestParser.TryParse(request, out var parsed, out var error);

        Assert.True(ok, error);
        Assert.Equal(Optimisarr.Core.Queue.VideoQualityStrategy.Fixed, parsed.VideoQualityStrategy);
        Assert.False(parsed.VmafQualityGateEnabled);
    }

    [Fact]
    public void Adaptive_quality_requires_an_enabled_vmaf_target()
    {
        var request = Request() with { VideoQualityStrategy = "AdaptiveVmaf" };

        var ok = LibraryRequestParser.TryParse(request, out _, out var error);

        Assert.False(ok);
        Assert.Contains("VMAF target", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Adaptive_quality_is_accepted_for_a_video_reencode_with_vmaf()
    {
        var request = Request() with
        {
            VideoQualityStrategy = "AdaptiveVmaf",
            VmafQualityGateEnabled = true
        };

        var ok = LibraryRequestParser.TryParse(request, out var parsed, out var error);

        Assert.True(ok, error);
        Assert.Equal(Optimisarr.Core.Queue.VideoQualityStrategy.AdaptiveVmaf, parsed.VideoQualityStrategy);
    }

    [Fact]
    public void Undefined_numeric_quality_strategy_is_rejected()
    {
        var request = Request() with { VideoQualityStrategy = "999" };

        var ok = LibraryRequestParser.TryParse(request, out _, out var error);

        Assert.False(ok);
        Assert.Contains("Unknown video quality strategy", error);
    }

    [Fact]
    public void Complete_vmaf_override_is_preserved()
    {
        var request = Request() with
        {
            VmafQualityGateEnabled = true,
            MinVmafHarmonicMean = 90,
            MinVmafMin = 75,
            MinVmafCatastrophicMin = 45,
            ClipVmafEnabled = true,
            VmafFrameSubsample = 2
        };

        var ok = LibraryRequestParser.TryParse(request, out var parsed, out var error);

        Assert.True(ok, error);
        Assert.True(parsed.VmafQualityGateEnabled);
        Assert.Equal(45, parsed.MinVmafCatastrophicMin);
        Assert.True(parsed.ClipVmafEnabled);
        Assert.Equal(2, parsed.VmafFrameSubsample);
    }

    [Fact]
    public void Omitted_library_verification_policy_uses_safe_defaults()
    {
        var ok = LibraryRequestParser.TryParse(Request(), out var parsed, out var error);

        Assert.True(ok, error);
        Assert.Equal(VerificationPolicy.Default.DurationTolerancePercent, parsed.DurationTolerancePercent);
        Assert.Equal(VerificationPolicy.Default.RequireAudioRetained, parsed.RequireAudioRetained);
        Assert.Equal(VerificationPolicy.Default.RequireSubtitlesRetained, parsed.RequireSubtitlesRetained);
        Assert.Equal(VerificationPolicy.Default.RequireSizeReduction, parsed.RequireSizeReduction);
        Assert.Equal(VerificationPolicy.Default.ImageQualityGateEnabled, parsed.ImageQualityGateEnabled);
        Assert.Equal(VerificationPolicy.Default.MinimumImageSsim, parsed.MinimumImageSsim);
        Assert.Equal(VerificationPolicy.Default.ImageMetadataGateEnabled, parsed.ImageMetadataGateEnabled);
    }

    [Theory]
    [InlineData(-0.1, 1, 0.95, "duration")]
    [InlineData(1, -0.1, 0.95, "loudness")]
    [InlineData(1, 1, -0.01, "SSIM")]
    [InlineData(1, 1, 1.01, "SSIM")]
    public void Invalid_library_verification_thresholds_are_rejected(
        double durationTolerance,
        double loudnessDrift,
        double imageSsim,
        string expected)
    {
        var request = Request() with
        {
            DurationTolerancePercent = durationTolerance,
            MaxLoudnessDriftLufs = loudnessDrift,
            MinimumImageSsim = imageSsim
        };

        var ok = LibraryRequestParser.TryParse(request, out _, out var error);

        Assert.False(ok);
        Assert.Contains(expected, error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Vmaf_floors_must_be_ordered_from_catastrophic_to_overall()
    {
        var request = Request() with
        {
            MinVmafHarmonicMean = 80,
            MinVmafMin = 85,
            MinVmafCatastrophicMin = 90
        };

        var ok = LibraryRequestParser.TryParse(request, out _, out var error);

        Assert.False(ok);
        Assert.Contains("catastrophic", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("quick", "quick")]
    [InlineData("fast", "fast")]
    [InlineData("P7", "p7")]
    [InlineData("very slow", null)]
    public void Encoder_effort_is_normalised_or_rejected(string value, string? expected)
    {
        var ok = LibraryRequestParser.TryParse(
            Request(encoderPreset: value),
            out var parsed,
            out var error);

        if (expected is null)
        {
            Assert.False(ok);
            Assert.Contains("encoder effort", error, StringComparison.OrdinalIgnoreCase);
            return;
        }

        Assert.True(ok, error);
        Assert.Equal(expected, parsed.EncoderPreset);
    }

    [Fact]
    public void Kept_audio_languages_are_normalised_to_lower_case_codes()
    {
        var ok = LibraryRequestParser.TryParse(Request(" ENG , jpn ,eng"), out var parsed, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal("eng, jpn", parsed.KeepAudioLanguages);
    }

    [Fact]
    public void Blank_kept_audio_languages_store_null_meaning_keep_everything()
    {
        var ok = LibraryRequestParser.TryParse(Request("   "), out var parsed, out _);

        Assert.True(ok);
        Assert.Null(parsed.KeepAudioLanguages);
    }

    [Theory]
    [InlineData("english")]
    [InlineData("e")]
    [InlineData("en1")]
    [InlineData("eng; jpn")]
    public void Kept_audio_languages_reject_anything_but_iso_639_codes(string value)
    {
        var ok = LibraryRequestParser.TryParse(Request(value), out _, out var error);

        Assert.False(ok);
        Assert.Contains("ISO 639", error);
    }

    [Fact]
    public void Kept_subtitle_languages_are_normalised_to_lower_case_codes()
    {
        var ok = LibraryRequestParser.TryParse(
            Request(keepSubtitleLanguages: " EN , jpn, fre, eng"), out var parsed, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal("eng, jpn, fra", parsed.KeepSubtitleLanguages);
    }

    [Fact]
    public void Blank_kept_subtitle_languages_store_null_meaning_keep_everything()
    {
        var ok = LibraryRequestParser.TryParse(Request(keepSubtitleLanguages: "   "), out var parsed, out _);

        Assert.True(ok);
        Assert.Null(parsed.KeepSubtitleLanguages);
    }

    [Theory]
    [InlineData("english")]
    [InlineData("eng; jpn")]
    [InlineData("qqq")]
    [InlineData("afa")]
    public void Kept_subtitle_languages_reject_anything_but_iso_639_codes(string value)
    {
        var ok = LibraryRequestParser.TryParse(Request(keepSubtitleLanguages: value), out _, out var error);

        Assert.False(ok);
        Assert.Contains("Subtitle languages", error);
    }

    [Theory]
    [InlineData("Music")]
    [InlineData("Photo")]
    public void Track_cleanup_rejects_media_types_that_cannot_contain_video(string mediaType)
    {
        var ok = LibraryRequestParser.TryParse(
            Request() with { MediaType = mediaType, RuleProfile = "TrackCleanup" },
            out _,
            out var error);

        Assert.False(ok);
        Assert.Contains("video", error, StringComparison.OrdinalIgnoreCase);
    }
}
