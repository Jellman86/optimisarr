using Optimisarr.Api.Library;

namespace Optimisarr.Tests;

public sealed class LibraryRequestParserTests
{
    // A baseline valid request; the path must exist because the parser verifies it.
    private static SaveLibraryRequest Request(string? keepAudioLanguages = null) => new(
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
        AutoEnqueueEnabled: null,
        AutoEnqueueWindowStart: null,
        AutoEnqueueWindowEnd: null,
        AutoReplace: null);

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
}
