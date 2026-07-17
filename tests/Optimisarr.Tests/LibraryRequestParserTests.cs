using Optimisarr.Api.Library;

namespace Optimisarr.Tests;

public sealed class LibraryRequestParserTests
{
    // A baseline valid request; the path must exist because the parser verifies it.
    private static SaveLibraryRequest Request(
        string? keepAudioLanguages = null, string? keepSubtitleLanguages = null) => new(
        Name: "Films",
        Path: Path.GetTempPath(),
        MediaType: "Film",
        RuleProfile: "ConservativeHevc",
        Enabled: true,
        Priority: 0,
        MinFileSizeBytes: null,
        MaxHeight: null,
        ReencodeSameCodecAboveBytes: null,
        SkipEfficientSources: null,
        TargetVideoCodec: null,
        TargetContainer: null,
        HdrHandling: null,
        OptimiseDolbyVision: null,
        ExcludePaths: null,
        QualityCrf: null,
        EncoderPreset: null,
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
        AutoReplace: null);

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
