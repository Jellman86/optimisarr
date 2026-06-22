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
        // The Conservative HEVC profile targets MP4 for broad device compatibility.
        Assert.Equal("/work/Movie/Movie.mp4", spec.OutputPath);
    }

    [Fact]
    public void Falls_back_to_mkv_when_an_mp4_target_meets_image_subtitles()
    {
        // MP4 can't store PGS/VobSub, so a source with image subtitles must go to MKV instead.
        var spec = TranscodeSpecResolver.Resolve(
            Hevc, inputPath: "/data/films/Movie/Movie.mkv", relativePath: "Movie/Movie.mkv",
            workRoot: "/work", sourceIsHdr: false, crf: 23, preset: "medium",
            kind: MediaKind.Video, sourceHasImageSubtitles: true);

        Assert.Equal("/work/Movie/Movie.mkv", spec.OutputPath);
    }

    [Fact]
    public void Keeps_the_mp4_target_when_there_are_no_image_subtitles()
    {
        var spec = TranscodeSpecResolver.Resolve(
            Hevc, inputPath: "/data/films/Movie/Movie.mkv", relativePath: "Movie/Movie.mkv",
            workRoot: "/work", sourceIsHdr: false, crf: 23, preset: "medium",
            kind: MediaKind.Video, sourceHasImageSubtitles: false);

        Assert.Equal("/work/Movie/Movie.mp4", spec.OutputPath);
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
    public void An_image_kind_resolves_to_a_jpeg_spec_with_the_default_encoder_and_quality()
    {
        var spec = TranscodeSpecResolver.Resolve(
            Hevc, inputPath: "/data/photos/2024/IMG_1.png", relativePath: "2024/IMG_1.png",
            workRoot: "/work", sourceIsHdr: false, crf: null, preset: null, kind: MediaKind.Image);

        Assert.Equal(MediaKind.Image, spec.Kind);
        Assert.Equal("/work/2024/IMG_1.jpg", spec.OutputPath);
        Assert.Equal("mjpeg", spec.ImageEncoder);
        Assert.Equal(80, spec.ImageQuality);
        // No downscale by default.
        Assert.Null(spec.ImageScaleFilter);
        // The video and audio fields stay empty for an image job.
        Assert.Null(spec.VideoCodec);
        Assert.Null(spec.AudioEncoder);
    }

    [Fact]
    public void An_image_downscale_override_resolves_a_scale_filter_into_the_spec()
    {
        var rules = Hevc with
        {
            ImageDownscaleMode = ImageDownscaleMode.MaxLongEdge,
            ImageDownscaleValue = 1920
        };

        var spec = TranscodeSpecResolver.Resolve(
            rules, inputPath: "/data/photos/IMG.png", relativePath: "IMG.png",
            workRoot: "/work", sourceIsHdr: false, crf: null, preset: null, kind: MediaKind.Image);

        Assert.NotNull(spec.ImageScaleFilter);
        Assert.Contains("min(iw,1920)", spec.ImageScaleFilter);
    }

    [Fact]
    public void An_audio_target_override_picks_the_right_encoder_container_and_bitrate()
    {
        var rules = Hevc with { TargetAudioCodec = "aac", AudioBitrateKbps = 192 };

        var spec = TranscodeSpecResolver.Resolve(
            rules, inputPath: "/data/music/Album/Track.flac", relativePath: "Album/Track.flac",
            workRoot: "/work", sourceIsHdr: false, crf: null, preset: null, kind: MediaKind.Audio);

        Assert.Equal("/work/Album/Track.m4a", spec.OutputPath);
        Assert.Equal("aac", spec.AudioEncoder);
        Assert.Equal(192, spec.AudioBitrateKbps);
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
    public void A_video_job_copies_audio_by_default()
    {
        var spec = TranscodeSpecResolver.Resolve(
            Hevc, "/data/a.mkv", "a.mkv", "/work", sourceIsHdr: false, crf: 23, preset: "medium");

        Assert.Null(spec.AudioEncoder);
        Assert.Null(spec.AudioBitrateKbps);
    }

    [Fact]
    public void A_video_job_re_encodes_audio_when_the_library_opts_in()
    {
        var rules = Hevc with { VideoAudioCodec = "aac", VideoAudioBitrateKbps = 160 };

        var spec = TranscodeSpecResolver.Resolve(
            rules, "/data/a.mkv", "a.mkv", "/work", sourceIsHdr: false, crf: 23, preset: "medium");

        Assert.Equal("hevc", spec.VideoCodec);
        Assert.Equal("aac", spec.AudioEncoder);
        Assert.Equal(160, spec.AudioBitrateKbps);
        // The output container follows the profile's target (MP4 for HEVC), not the audio choice.
        Assert.Equal("/work/a.mp4", spec.OutputPath);
    }

    [Fact]
    public void An_audio_job_downmixes_to_stereo_when_the_library_opts_in()
    {
        var rules = Hevc with { DownmixToStereo = true };

        var spec = TranscodeSpecResolver.Resolve(
            rules, "/data/music/Track.flac", "Track.flac", "/work",
            sourceIsHdr: false, crf: null, preset: null, kind: MediaKind.Audio);

        Assert.True(spec.DownmixToStereo);
    }

    [Fact]
    public void A_video_downmix_only_applies_when_its_audio_is_re_encoded()
    {
        // Downmix requires an audio re-encode; with audio copied (no video-audio codec) the
        // flag is not set, so a copied track keeps its layout.
        var copyAudio = TranscodeSpecResolver.Resolve(
            Hevc with { DownmixToStereo = true },
            "/data/a.mkv", "a.mkv", "/work", sourceIsHdr: false, crf: 23, preset: "medium");
        Assert.False(copyAudio.DownmixToStereo);

        var reencodeAudio = TranscodeSpecResolver.Resolve(
            Hevc with { DownmixToStereo = true, VideoAudioCodec = "aac" },
            "/data/a.mkv", "a.mkv", "/work", sourceIsHdr: false, crf: 23, preset: "medium");
        Assert.True(reencodeAudio.DownmixToStereo);
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
