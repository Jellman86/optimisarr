using Optimisarr.Core.Domain;
using Optimisarr.Core.Queue;

namespace Optimisarr.Tests;

public sealed class FfmpegCommandBuilderTests
{
    private static TranscodeSpec AudioReencode() =>
        new(
            InputPath: "/data/music/Track.flac",
            OutputPath: "/work/Track.opus",
            VideoCodec: null,
            Crf: null,
            Preset: null,
            TonemapToSdr: false,
            Kind: MediaKind.Audio,
            AudioEncoder: "libopus",
            AudioBitrateKbps: 128);

    [Fact]
    public void An_audio_job_re_encodes_audio_to_the_target_codec_and_bitrate()
    {
        var args = FfmpegCommandBuilder.Build(AudioReencode());

        var audioCodecIndex = IndexOf(args, "-c:a");
        Assert.Equal("libopus", args[audioCodecIndex + 1]);
        var bitrateIndex = IndexOf(args, "-b:a");
        Assert.Equal("128k", args[bitrateIndex + 1]);
        Assert.Equal("/work/Track.opus", args[^1]);
    }

    [Fact]
    public void An_audio_job_does_not_re_encode_video_and_preserves_cover_art_and_metadata()
    {
        var args = FfmpegCommandBuilder.Build(AudioReencode());

        // Cover art is copied, not re-encoded; metadata is carried over.
        var videoCodecIndex = IndexOf(args, "-c:v");
        Assert.Equal("copy", args[videoCodecIndex + 1]);
        Assert.DoesNotContain("libx265", args);
        Assert.DoesNotContain("-crf", args);
        var metaMapIndex = IndexOf(args, "-map_metadata");
        Assert.Equal("0", args[metaMapIndex + 1]);
    }

    [Fact]
    public void An_audio_job_still_stamps_the_optimisation_marker()
    {
        var args = FfmpegCommandBuilder.Build(AudioReencode(), optimisedMarker: "0.4.2");

        var metaIndex = IndexOf(args, "-metadata");
        Assert.Equal("optimisarr=0.4.2", args[metaIndex + 1]);
    }

    private static TranscodeSpec ImageReencode(string encoder = "libwebp", int? quality = 80) =>
        new(
            InputPath: "/data/photos/IMG.png",
            OutputPath: "/work/IMG.webp",
            VideoCodec: null,
            Crf: null,
            Preset: null,
            TonemapToSdr: false,
            Kind: MediaKind.Image,
            ImageEncoder: encoder,
            ImageQuality: quality);

    [Fact]
    public void An_image_job_re_encodes_to_the_target_encoder_with_quality_and_preserves_metadata()
    {
        var args = FfmpegCommandBuilder.Build(ImageReencode());

        var codecIndex = IndexOf(args, "-c:v");
        Assert.Equal("libwebp", args[codecIndex + 1]);
        var qualityIndex = IndexOf(args, "-quality");
        Assert.Equal("80", args[qualityIndex + 1]);
        // EXIF/ICC and other metadata are carried over from the source image.
        var metaMapIndex = IndexOf(args, "-map_metadata");
        Assert.Equal("0", args[metaMapIndex + 1]);
        Assert.Equal("/work/IMG.webp", args[^1]);
        // An image job has no audio/subtitle stream handling.
        Assert.DoesNotContain("-c:a", args);
        Assert.DoesNotContain("-c:s", args);
    }

    [Fact]
    public void An_image_job_stamps_the_optimisation_marker()
    {
        var args = FfmpegCommandBuilder.Build(ImageReencode(), optimisedMarker: "0.4.2");

        var metaIndex = IndexOf(args, "-metadata");
        Assert.Equal("optimisarr=0.4.2", args[metaIndex + 1]);
    }

    [Fact]
    public void A_jpeg_image_job_maps_quality_onto_mjpeg_qv_scale()
    {
        // mjpeg uses -q:v 2 (best) … 31 (worst); our 0–100 quality (higher better) inverts onto it.
        var args = FfmpegCommandBuilder.Build(ImageReencode(encoder: "mjpeg", quality: 100));

        Assert.Equal("mjpeg", args[IndexOf(args, "-c:v") + 1]);
        Assert.Equal("2", args[IndexOf(args, "-q:v") + 1]);
        Assert.DoesNotContain("-quality", args);
    }

    [Fact]
    public void An_avif_image_job_uses_constant_quality_crf_and_still_picture()
    {
        var args = FfmpegCommandBuilder.Build(ImageReencode(encoder: "libaom-av1", quality: 100));

        Assert.Equal("libaom-av1", args[IndexOf(args, "-c:v") + 1]);
        // Best quality (100) maps to CRF 0 with a zero target bitrate (constant-quality mode).
        Assert.Equal("0", args[IndexOf(args, "-crf") + 1]);
        Assert.Equal("0", args[IndexOf(args, "-b:v") + 1]);
        Assert.Equal("1", args[IndexOf(args, "-still-picture") + 1]);
        Assert.Equal("yuv420p", args[IndexOf(args, "-pix_fmt") + 1]);
    }

    [Fact]
    public void Image_encoding_for_an_unknown_encoder_still_throws()
    {
        Assert.Throws<NotSupportedException>(() =>
            FfmpegCommandBuilder.Build(ImageReencode(encoder: "libjxl")));
    }

    [Fact]
    public void An_image_job_applies_a_downscale_filter_before_the_encoder()
    {
        var spec = ImageReencode() with
        {
            ImageScaleFilter = "scale=w='if(gt(iw,ih),min(iw,1920),-2)':h='if(gt(iw,ih),-2,min(ih,1920))':flags=lanczos"
        };

        var args = FfmpegCommandBuilder.Build(spec);

        var vfIndex = IndexOf(args, "-vf");
        Assert.Contains("scale=", args[vfIndex + 1]);
        // The filter must precede the codec selection.
        Assert.True(vfIndex < IndexOf(args, "-c:v"));
    }

    private static TranscodeSpec Reencode(
        string? videoCodec = "hevc",
        int? crf = 23,
        string? preset = "medium",
        bool tonemap = false) =>
        new(
            InputPath: "/data/films/Movie.mkv",
            OutputPath: "/work/Movie.opt.mkv",
            VideoCodec: videoCodec,
            Crf: crf,
            Preset: preset,
            TonemapToSdr: tonemap);

    private static int IndexOf(IReadOnlyList<string> args, string value) =>
        ((List<string>)args).IndexOf(value);

    [Fact]
    public void Passes_input_and_output_paths_as_separate_arguments()
    {
        var args = FfmpegCommandBuilder.Build(Reencode());

        // The path is its own argument (never interpolated into a shell string).
        var inputIndex = IndexOf(args, "-i");
        Assert.Equal("/data/films/Movie.mkv", args[inputIndex + 1]);
        Assert.Equal("/work/Movie.opt.mkv", args[^1]);
    }

    [Fact]
    public void Always_overwrites_its_own_work_output_and_maps_all_streams()
    {
        var args = FfmpegCommandBuilder.Build(Reencode());

        Assert.Contains("-y", args);
        var mapIndex = IndexOf(args, "-map");
        Assert.Equal("0", args[mapIndex + 1]);
    }

    [Fact]
    public void Adds_global_thread_limit_when_configured()
    {
        var args = FfmpegCommandBuilder.Build(Reencode(), threads: 2);

        var threadsIndex = IndexOf(args, "-threads");
        Assert.Equal("2", args[threadsIndex + 1]);
        Assert.True(threadsIndex < IndexOf(args, "-i"));
    }

    [Fact]
    public void Omits_thread_limit_when_zero_or_negative()
    {
        Assert.DoesNotContain("-threads", FfmpegCommandBuilder.Build(Reencode(), threads: 0));
        Assert.DoesNotContain("-threads", FfmpegCommandBuilder.Build(Reencode(), threads: -1));
    }

    [Theory]
    [InlineData("hevc", "libx265")]
    [InlineData("h264", "libx264")]
    [InlineData("av1", "libsvtav1")]
    public void Maps_target_codec_to_the_expected_encoder(string codec, string encoder)
    {
        var args = FfmpegCommandBuilder.Build(Reencode(videoCodec: codec));

        var vIndex = IndexOf(args, "-c:v");
        Assert.Equal(encoder, args[vIndex + 1]);
    }

    [Fact]
    public void Uses_selected_video_encoder_when_provided()
    {
        var args = FfmpegCommandBuilder.Build(Reencode(videoCodec: "hevc"), videoEncoder: "hevc_nvenc");

        var vIndex = IndexOf(args, "-c:v");
        Assert.Equal("hevc_nvenc", args[vIndex + 1]);
    }

    [Fact]
    public void Nvenc_uses_constant_quality_cq_not_crf()
    {
        var args = FfmpegCommandBuilder.Build(Reencode(crf: 24), videoEncoder: "hevc_nvenc");

        // NVENC ignores -crf; it takes -cq for constant quality with a zero target bitrate.
        Assert.DoesNotContain("-crf", args);
        Assert.Equal("24", args[IndexOf(args, "-cq") + 1]);
        Assert.Equal("0", args[IndexOf(args, "-b:v") + 1]);
        Assert.Equal("vbr", args[IndexOf(args, "-rc") + 1]);
        // NVENC encodes from software-decoded frames; no hardware device init needed.
        Assert.DoesNotContain("-vaapi_device", args);
        Assert.DoesNotContain("-init_hw_device", args);
    }

    [Fact]
    public void Vaapi_inits_the_device_before_input_and_uses_qp_and_hwupload()
    {
        var args = FfmpegCommandBuilder.Build(Reencode(crf: 24, preset: "slow"), videoEncoder: "hevc_vaapi");

        // The VAAPI device must be declared before -i.
        var deviceIndex = IndexOf(args, "-vaapi_device");
        Assert.Equal("/dev/dri/renderD128", args[deviceIndex + 1]);
        Assert.True(deviceIndex < IndexOf(args, "-i"));

        // Frames are uploaded to the GPU before the encoder.
        var vfIndex = IndexOf(args, "-vf");
        Assert.Contains("format=nv12,hwupload", args[vfIndex + 1]);

        // VAAPI uses CQP/-qp, never -crf, and has no -preset.
        Assert.DoesNotContain("-crf", args);
        Assert.Equal("CQP", args[IndexOf(args, "-rc_mode") + 1]);
        Assert.Equal("24", args[IndexOf(args, "-qp") + 1]);
        Assert.DoesNotContain("-preset", args);
    }

    [Fact]
    public void Vaapi_combines_tonemap_then_hwupload_in_one_filter_chain()
    {
        var args = FfmpegCommandBuilder.Build(Reencode(tonemap: true), videoEncoder: "hevc_vaapi");

        var chain = args[IndexOf(args, "-vf") + 1];
        // Tone-map to SDR happens in software, then the result is uploaded to the GPU.
        Assert.True(chain.IndexOf("tonemap", StringComparison.Ordinal)
            < chain.IndexOf("hwupload", StringComparison.Ordinal));
    }

    [Fact]
    public void Qsv_inits_the_device_and_uses_global_quality()
    {
        var args = FfmpegCommandBuilder.Build(Reencode(crf: 24), videoEncoder: "hevc_qsv");

        var initIndex = IndexOf(args, "-init_hw_device");
        Assert.Equal("qsv=hw", args[initIndex + 1]);
        Assert.True(initIndex < IndexOf(args, "-i"));

        var vfIndex = IndexOf(args, "-vf");
        Assert.Contains("format=qsv", args[vfIndex + 1]);

        Assert.DoesNotContain("-crf", args);
        Assert.Equal("24", args[IndexOf(args, "-global_quality") + 1]);
    }

    [Fact]
    public void Applies_crf_and_preset_when_re_encoding()
    {
        var args = FfmpegCommandBuilder.Build(Reencode(crf: 28, preset: "slow"));

        var crfIndex = IndexOf(args, "-crf");
        Assert.Equal("28", args[crfIndex + 1]);
        var presetIndex = IndexOf(args, "-preset");
        Assert.Equal("slow", args[presetIndex + 1]);
    }

    [Fact]
    public void Copies_audio_and_subtitles_when_re_encoding_video()
    {
        var args = FfmpegCommandBuilder.Build(Reencode());

        var audioIndex = IndexOf(args, "-c:a");
        Assert.Equal("copy", args[audioIndex + 1]);
        var subIndex = IndexOf(args, "-c:s");
        Assert.Equal("copy", args[subIndex + 1]);
    }

    [Fact]
    public void Remux_only_copies_all_streams_and_never_re_encodes()
    {
        var args = FfmpegCommandBuilder.Build(Reencode(videoCodec: null));

        Assert.DoesNotContain("-c:v", args);
        Assert.DoesNotContain("-crf", args);
        var cIndex = IndexOf(args, "-c");
        Assert.Equal("copy", args[cIndex + 1]);
    }

    [Fact]
    public void Adds_a_tonemap_filter_when_converting_hdr_to_sdr()
    {
        var args = FfmpegCommandBuilder.Build(Reencode(tonemap: true));

        var vfIndex = IndexOf(args, "-vf");
        Assert.True(vfIndex >= 0);
        Assert.Contains("tonemap", args[vfIndex + 1]);
    }

    [Fact]
    public void Does_not_add_a_tonemap_filter_otherwise()
    {
        var args = FfmpegCommandBuilder.Build(Reencode(tonemap: false));

        Assert.DoesNotContain("-vf", args);
    }

    [Fact]
    public void Stamps_the_optimisation_marker_into_the_output_metadata()
    {
        var args = FfmpegCommandBuilder.Build(Reencode(), optimisedMarker: "0.4.2");

        var metaIndex = IndexOf(args, "-metadata");
        Assert.True(metaIndex >= 0);
        Assert.Equal("optimisarr=0.4.2", args[metaIndex + 1]);
        // The marker is an output option, before the output path.
        Assert.True(metaIndex < args.Count - 1);
    }

    [Fact]
    public void Stamps_the_marker_on_a_remux_too()
    {
        var args = FfmpegCommandBuilder.Build(Reencode(videoCodec: null), optimisedMarker: "0.4.2");

        var metaIndex = IndexOf(args, "-metadata");
        Assert.Equal("optimisarr=0.4.2", args[metaIndex + 1]);
    }

    [Fact]
    public void Adds_the_mp4_flag_so_custom_tags_round_trip_for_mp4_outputs()
    {
        var spec = Reencode() with { OutputPath = "/work/Movie.opt.mp4" };

        var args = FfmpegCommandBuilder.Build(spec, optimisedMarker: "0.4.2");

        var flagsIndex = IndexOf(args, "-movflags");
        Assert.True(flagsIndex >= 0);
        Assert.Equal("use_metadata_tags", args[flagsIndex + 1]);
    }

    [Fact]
    public void Does_not_add_the_mp4_flag_for_a_matroska_output()
    {
        var args = FfmpegCommandBuilder.Build(Reencode(), optimisedMarker: "0.4.2");

        Assert.DoesNotContain("-movflags", args);
    }

    [Fact]
    public void Omits_the_marker_when_none_is_given()
    {
        var args = FfmpegCommandBuilder.Build(Reencode());

        Assert.DoesNotContain("-metadata", args);
    }

    [Fact]
    public void Re_encodes_a_videos_audio_when_an_audio_encoder_is_set()
    {
        var spec = Reencode() with { AudioEncoder = "aac", AudioBitrateKbps = 160 };

        var args = FfmpegCommandBuilder.Build(spec);

        var audioIndex = IndexOf(args, "-c:a");
        Assert.Equal("aac", args[audioIndex + 1]);
        var bitrateIndex = IndexOf(args, "-b:a");
        Assert.Equal("160k", args[bitrateIndex + 1]);
        // Video is still re-encoded and subtitles copied.
        Assert.Equal("libx265", args[IndexOf(args, "-c:v") + 1]);
        Assert.Equal("copy", args[IndexOf(args, "-c:s") + 1]);
    }

    [Fact]
    public void Re_encodes_the_audio_of_a_remux_only_job_when_requested()
    {
        var spec = Reencode(videoCodec: null) with { AudioEncoder = "aac", AudioBitrateKbps = 160 };

        var args = FfmpegCommandBuilder.Build(spec);

        // Video and subtitles still copy via the blanket "-c copy"; only audio is overridden.
        Assert.Equal("copy", args[IndexOf(args, "-c") + 1]);
        Assert.DoesNotContain("-c:v", args);
        var audioIndex = IndexOf(args, "-c:a");
        Assert.Equal("aac", args[audioIndex + 1]);
        Assert.Equal("160k", args[IndexOf(args, "-b:a") + 1]);
    }

    [Fact]
    public void Copies_audio_with_no_bitrate_when_no_audio_encoder_is_set()
    {
        var args = FfmpegCommandBuilder.Build(Reencode());

        Assert.Equal("copy", args[IndexOf(args, "-c:a") + 1]);
        Assert.DoesNotContain("-b:a", args);
    }

    [Fact]
    public void Downmixes_an_audio_job_to_stereo_when_requested()
    {
        var spec = AudioReencode() with { DownmixToStereo = true };

        var args = FfmpegCommandBuilder.Build(spec);

        var acIndex = IndexOf(args, "-ac");
        Assert.Equal("2", args[acIndex + 1]);
    }

    [Fact]
    public void Downmixes_a_videos_re_encoded_audio_to_stereo_when_requested()
    {
        var spec = Reencode() with { AudioEncoder = "aac", AudioBitrateKbps = 160, DownmixToStereo = true };

        var args = FfmpegCommandBuilder.Build(spec);

        Assert.Equal("aac", args[IndexOf(args, "-c:a") + 1]);
        Assert.Equal("2", args[IndexOf(args, "-ac") + 1]);
    }

    [Fact]
    public void Does_not_downmix_when_not_requested()
    {
        Assert.DoesNotContain("-ac", FfmpegCommandBuilder.Build(AudioReencode()));
        Assert.DoesNotContain("-ac", FfmpegCommandBuilder.Build(Reencode()));
    }
}
