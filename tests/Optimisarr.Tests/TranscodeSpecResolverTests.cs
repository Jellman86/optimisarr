using Optimisarr.Core.Domain;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Rules;

namespace Optimisarr.Tests;

public sealed class TranscodeSpecResolverTests
{
    private static readonly RuleSettings Hevc = RuleProfileDefaults.For(RuleProfile.ConservativeHevc);

    [Fact]
    public void Output_goes_under_the_work_root_with_the_target_container_extension()
    {
        var spec = TranscodeSpecResolver.Resolve(
            Hevc, inputPath: "/data/films/Movie/Movie.avi", relativePath: "Movie/Movie.avi",
            workRoot: "/work", sourceIsHdr: false, crf: 23, preset: "medium");

        Assert.Equal("/data/films/Movie/Movie.avi", spec.InputPath);
        Assert.Equal("/work/Movie/Movie.mkv", spec.OutputPath);
    }

    [Fact]
    public void An_audio_kind_resolves_to_an_audio_spec_with_the_default_target()
    {
        var spec = TranscodeSpecResolver.Resolve(
            Hevc, inputPath: "/data/music/Album/Track.flac", relativePath: "Album/Track.flac",
            workRoot: "/work", sourceIsHdr: false, crf: 23, preset: "medium", kind: MediaKind.Audio);

        Assert.Equal(MediaKind.Audio, spec.Kind);
        Assert.Equal("/work/Album/Track.opus", spec.OutputPath);
        Assert.Equal("libopus", spec.AudioEncoder);
        Assert.Equal(128, spec.AudioBitrateKbps);
        // The video fields stay empty for an audio job.
        Assert.Null(spec.VideoCodec);
        Assert.Null(spec.Crf);
    }

    [Fact]
    public void Carries_the_target_codec_crf_and_preset()
    {
        var spec = TranscodeSpecResolver.Resolve(
            Hevc, "/data/a.mkv", "a.mkv", "/work", sourceIsHdr: false, crf: 28, preset: "slow");

        Assert.Equal("hevc", spec.VideoCodec);
        Assert.Equal(28, spec.Crf);
        Assert.Equal("slow", spec.Preset);
    }

    [Fact]
    public void Remux_profile_produces_a_copy_spec_with_no_codec()
    {
        var remux = RuleProfileDefaults.For(RuleProfile.RemuxCleanup);

        var spec = TranscodeSpecResolver.Resolve(
            remux, "/data/a.avi", "a.avi", "/work", sourceIsHdr: false, crf: null, preset: null);

        Assert.Null(spec.VideoCodec);
        Assert.False(spec.TonemapToSdr);
    }

    [Fact]
    public void Tonemaps_only_when_the_source_is_hdr_and_the_rule_says_so()
    {
        var tonemapRules = Hevc with { Hdr = HdrHandling.TonemapToSdr };

        var hdrSpec = TranscodeSpecResolver.Resolve(
            tonemapRules, "/data/a.mkv", "a.mkv", "/work", sourceIsHdr: true, crf: 23, preset: "medium");
        var sdrSpec = TranscodeSpecResolver.Resolve(
            tonemapRules, "/data/b.mkv", "b.mkv", "/work", sourceIsHdr: false, crf: 23, preset: "medium");
        var preserveSpec = TranscodeSpecResolver.Resolve(
            Hevc with { Hdr = HdrHandling.Preserve }, "/data/c.mkv", "c.mkv", "/work", sourceIsHdr: true, crf: 23, preset: "medium");

        Assert.True(hdrSpec.TonemapToSdr);
        Assert.False(sdrSpec.TonemapToSdr);   // not HDR
        Assert.False(preserveSpec.TonemapToSdr); // HDR but rule preserves it
    }
}
