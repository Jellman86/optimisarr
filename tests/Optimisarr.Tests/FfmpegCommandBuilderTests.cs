using Optimisarr.Core.Domain;
using Optimisarr.Core.Queue;

namespace Optimisarr.Tests;

public sealed class FfmpegCommandBuilderTests
{
    private static TranscodeSpec AudioReencode() =>
        new(
            InputPath: "/data/music/Track.flac",
            OutputPath: "/work/Track.m4a",
            VideoCodec: null,
            Crf: null,
            Preset: null,
            TonemapToSdr: false,
            Kind: MediaKind.Audio,
            AudioEncoder: "aac",
            AudioBitrateKbps: 128);

    [Fact]
    public void An_audio_job_re_encodes_audio_to_the_target_codec_and_bitrate()
    {
        var args = FfmpegCommandBuilder.Build(AudioReencode());

        var audioCodecIndex = IndexOf(args, "-c:a");
        Assert.Equal("aac", args[audioCodecIndex + 1]);
        var bitrateIndex = IndexOf(args, "-b:a");
        Assert.Equal("128k", args[bitrateIndex + 1]);
        Assert.Equal("/work/Track.m4a", args[^1]);
    }

    [Fact]
    public void An_aac_audio_job_maps_metadata_and_timed_lyrics_but_not_unsafe_artwork()
    {
        var args = FfmpegCommandBuilder.Build(AudioReencode());

        Assert.DoesNotContain("0:v?", args);
        Assert.DoesNotContain("-c:v:0", args);
        Assert.DoesNotContain("libx265", args);
        Assert.DoesNotContain("-crf", args);
        var metaMapIndex = IndexOf(args, "-map_metadata");
        Assert.Equal("0", args[metaMapIndex + 1]);
        Assert.Contains("0:s?", args);
        Assert.Equal("mov_text", args[IndexOf(args, "-c:s") + 1]);
    }

    [Fact]
    public void An_mp3_audio_job_normalises_and_marks_cover_art_before_mapping_audio()
    {
        var spec = AudioReencode() with { OutputPath = "/work/Track.mp3", AudioEncoder = "libmp3lame" };

        var args = FfmpegCommandBuilder.Build(spec);

        Assert.Equal("mjpeg", args[IndexOf(args, "-c:v:0") + 1]);
        Assert.Equal("attached_pic", args[IndexOf(args, "-disposition:v:0") + 1]);
        Assert.True(IndexOf(args, "0:v?") < IndexOf(args, "0:a"));
        Assert.DoesNotContain("0:s?", args);
    }

    [Fact]
    public void An_opus_audio_job_maps_only_audio()
    {
        var spec = AudioReencode() with { OutputPath = "/work/Track.opus", AudioEncoder = "libopus" };

        var args = FfmpegCommandBuilder.Build(spec);

        Assert.DoesNotContain("0:v?", args);
        Assert.DoesNotContain("-c:v:0", args);
        Assert.DoesNotContain("0:s?", args);
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
    public void A_lossless_webp_image_job_uses_the_lossless_encoder_mode()
    {
        var args = FfmpegCommandBuilder.Build(ImageReencode() with { ImageLossless = true });

        Assert.Equal("1", args[IndexOf(args, "-lossless") + 1]);
    }

    [Fact]
    public void Image_encoding_for_an_unknown_encoder_still_throws()
    {
        Assert.Throws<NotSupportedException>(() =>
            FfmpegCommandBuilder.Build(ImageReencode(encoder: "libjxl")));
        Assert.Throws<NotSupportedException>(() =>
            FfmpegCommandBuilder.Build(ImageReencode(encoder: "libaom-av1")));
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

        var vIndex = IndexOf(args, "-c:v:0");
        Assert.Equal(encoder, args[vIndex + 1]);
    }

    [Fact]
    public void Uses_selected_video_encoder_when_provided()
    {
        var args = FfmpegCommandBuilder.Build(Reencode(videoCodec: "hevc"), videoEncoder: "hevc_nvenc");

        var vIndex = IndexOf(args, "-c:v:0");
        Assert.Equal("hevc_nvenc", args[vIndex + 1]);
    }

    [Fact]
    public void Re_encode_copies_all_streams_and_encodes_only_the_primary_video()
    {
        // Embedded cover art is an extra mjpeg/png video stream; encoding it through a hardware
        // encoder fails and aborts the job. The baseline "-c copy" keeps every stream (cover art,
        // attachments, data) and only v:0 (the real video) is re-encoded.
        var args = FfmpegCommandBuilder.Build(Reencode(videoCodec: "hevc"));

        var cIndex = IndexOf(args, "-c");
        Assert.Equal("copy", args[cIndex + 1]);
        Assert.Equal("libx265", args[IndexOf(args, "-c:v:0") + 1]);
        // The blanket "-c:v" (all video) is never used, which is what would catch the cover art.
        Assert.DoesNotContain("-c:v", args);
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
        var vfIndex = IndexOf(args, "-filter:v:0");
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

        var chain = args[IndexOf(args, "-filter:v:0") + 1];
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

        var vfIndex = IndexOf(args, "-filter:v:0");
        Assert.Contains("format=qsv", args[vfIndex + 1]);

        Assert.DoesNotContain("-crf", args);
        Assert.Equal("24", args[IndexOf(args, "-global_quality") + 1]);
    }

    [Fact]
    public void Qsv_hardware_decode_adds_hwaccel_before_input_and_drops_the_upload_filter()
    {
        var args = FfmpegCommandBuilder.Build(
            Reencode(crf: 24), videoEncoder: "hevc_qsv", hardwareDecode: true);

        // Decode happens on the GPU: -hwaccel qsv with qsv output frames, before -i.
        var hwaccelIndex = IndexOf(args, "-hwaccel");
        Assert.Equal("qsv", args[hwaccelIndex + 1]);
        Assert.Equal("qsv", args[IndexOf(args, "-hwaccel_output_format") + 1]);
        Assert.True(hwaccelIndex < IndexOf(args, "-i"));

        // Frames already live on the GPU, so there is no upload filter at all.
        Assert.DoesNotContain("-filter:v:0", args);
        // The encoder is still QSV with its constant-quality knob.
        Assert.Equal("hevc_qsv", args[IndexOf(args, "-c:v:0") + 1]);
        Assert.Equal("24", args[IndexOf(args, "-global_quality") + 1]);
    }

    [Fact]
    public void Vaapi_hardware_decode_adds_hwaccel_before_input_and_drops_the_upload_filter()
    {
        var args = FfmpegCommandBuilder.Build(
            Reencode(crf: 24), videoEncoder: "hevc_vaapi", hardwareDecode: true);

        var hwaccelIndex = IndexOf(args, "-hwaccel");
        Assert.Equal("vaapi", args[hwaccelIndex + 1]);
        Assert.Equal("vaapi", args[IndexOf(args, "-hwaccel_output_format") + 1]);
        Assert.True(hwaccelIndex < IndexOf(args, "-i"));
        Assert.True(IndexOf(args, "-vaapi_device") < IndexOf(args, "-i"));

        Assert.DoesNotContain("-filter:v:0", args);
        Assert.Equal("hevc_vaapi", args[IndexOf(args, "-c:v:0") + 1]);
    }

    [Fact]
    public void Hardware_decode_is_skipped_when_tone_mapping_so_the_software_filter_still_gets_frames()
    {
        // The tone-map runs in software and needs frames in system memory, so the source must
        // be software-decoded and re-uploaded — hardware decode cannot apply here.
        var args = FfmpegCommandBuilder.Build(
            Reencode(tonemap: true), videoEncoder: "hevc_qsv", hardwareDecode: true);

        Assert.DoesNotContain("-hwaccel", args);
        var chain = args[IndexOf(args, "-filter:v:0") + 1];
        Assert.Contains("tonemap", chain);
        Assert.Contains("hwupload", chain);
    }

    [Fact]
    public void Hardware_decode_is_ignored_for_a_software_encoder()
    {
        // A CPU encoder has no GPU device, so the flag is a no-op even when requested.
        var args = FfmpegCommandBuilder.Build(
            Reencode(videoCodec: "hevc"), hardwareDecode: true);

        Assert.DoesNotContain("-hwaccel", args);
        Assert.Equal("libx265", args[IndexOf(args, "-c:v:0") + 1]);
    }

    [Fact]
    public void Hardware_decode_off_keeps_the_qsv_upload_filter()
    {
        var args = FfmpegCommandBuilder.Build(
            Reencode(crf: 24), videoEncoder: "hevc_qsv", hardwareDecode: false);

        Assert.DoesNotContain("-hwaccel", args);
        Assert.Contains("format=qsv", args[IndexOf(args, "-filter:v:0") + 1]);
    }

    [Fact]
    public void Limits_output_to_the_clip_window_when_set()
    {
        var args = FfmpegCommandBuilder.Build(Reencode() with { ClipSeconds = 60 });

        // -t is an output option, before the output path.
        var clipIndex = IndexOf(args, "-t");
        Assert.Equal("60", args[clipIndex + 1]);
        Assert.True(clipIndex < args.Count - 1);
    }

    [Fact]
    public void Omits_the_clip_window_by_default()
    {
        Assert.DoesNotContain("-t", FfmpegCommandBuilder.Build(Reencode()));
        Assert.DoesNotContain("-ss", FfmpegCommandBuilder.Build(Reencode()));
    }

    [Fact]
    public void Seeks_to_the_clip_start_before_the_input()
    {
        var args = FfmpegCommandBuilder.Build(Reencode() with { ClipSeconds = 60, ClipStartSeconds = 1800 });

        // -ss is an input option: it must precede -i so the seek applies to the source.
        var seekIndex = IndexOf(args, "-ss");
        Assert.Equal("1800", args[seekIndex + 1]);
        Assert.True(seekIndex < IndexOf(args, "-i"));
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
    public void Excludes_the_removed_audio_tracks_from_a_video_re_encode()
    {
        var args = FfmpegCommandBuilder.Build(
            Reencode() with { RemoveAudioStreamIndexes = new[] { 1, 3 } });

        Assert.Contains(("-map", "-0:a:1"), MapPairs(args));
        Assert.Contains(("-map", "-0:a:3"), MapPairs(args));
        // The exclusions follow the blanket "-map 0" so they actually remove those streams.
        Assert.True(IndexOf(args, "0") < ((List<string>)args).IndexOf("-0:a:1"));
    }

    [Fact]
    public void Excludes_the_removed_audio_tracks_from_a_remux()
    {
        var args = FfmpegCommandBuilder.Build(
            Reencode(videoCodec: null, crf: null, preset: null) with { RemoveAudioStreamIndexes = new[] { 0 } });

        Assert.Contains(("-map", "-0:a:0"), MapPairs(args));
        // Still a pure stream copy: the kept tracks are not re-encoded.
        Assert.Equal("copy", args[IndexOf(args, "-c") + 1]);
    }

    [Fact]
    public void Removes_no_audio_tracks_by_default()
    {
        var args = FfmpegCommandBuilder.Build(Reencode());

        Assert.DoesNotContain(args, argument => argument.StartsWith("-0:a:"));
    }

    [Fact]
    public void Excludes_the_removed_subtitle_tracks_alongside_audio()
    {
        var args = FfmpegCommandBuilder.Build(
            Reencode(videoCodec: null, crf: null, preset: null) with
            {
                RemoveAudioStreamIndexes = new[] { 1 },
                RemoveSubtitleStreamIndexes = new[] { 0, 2 }
            });

        Assert.Contains(("-map", "-0:a:1"), MapPairs(args));
        Assert.Contains(("-map", "-0:s:0"), MapPairs(args));
        Assert.Contains(("-map", "-0:s:2"), MapPairs(args));
        // Still a pure stream copy: the kept tracks are not re-encoded.
        Assert.Equal("copy", args[IndexOf(args, "-c") + 1]);
        // The exclusions follow the blanket "-map 0" so they actually remove those streams.
        Assert.True(IndexOf(args, "0") < ((List<string>)args).IndexOf("-0:s:0"));
    }

    [Fact]
    public void Removes_no_subtitle_tracks_by_default()
    {
        var args = FfmpegCommandBuilder.Build(Reencode());

        Assert.DoesNotContain(args, argument => argument.StartsWith("-0:s:"));
    }

    [Theory]
    [InlineData(".mp4")]
    [InlineData(".m4v")]
    [InlineData(".mov")]
    public void Converts_subtitles_to_mov_text_for_mp4_family_video_outputs(string extension)
    {
        var args = FfmpegCommandBuilder.Build(Reencode() with { OutputPath = $"/work/Movie.opt{extension}" });

        var subIndex = IndexOf(args, "-c:s");
        Assert.Equal("mov_text", args[subIndex + 1]);
    }

    [Theory]
    [InlineData(".mp4")]
    [InlineData(".m4v")]
    [InlineData(".mov")]
    public void Drops_attachment_and_data_streams_for_mp4_family_video_outputs(string extension)
    {
        // A Matroska source can carry a font/cover attachment (codec "none") or a data stream.
        // MP4/MOV cannot mux those: ffmpeg reports "Could not find tag for codec none" and aborts
        // the whole job before writing a frame. They must be excluded so the file still transcodes.
        var args = FfmpegCommandBuilder.Build(Reencode() with { OutputPath = $"/work/Movie.opt{extension}" });

        Assert.Contains(("-map", "-0:t"), MapPairs(args));
        Assert.Contains(("-map", "-0:d"), MapPairs(args));
        // The exclusions follow the blanket "-map 0" so they actually remove those streams.
        Assert.True(IndexOf(args, "0") < ((List<string>)args).IndexOf("-0:t"));
    }

    [Fact]
    public void Keeps_attachment_and_data_streams_for_a_software_matroska_video_output()
    {
        // A CPU encode to Matroska can hold attachments and data streams, so the copy keeps them.
        var args = FfmpegCommandBuilder.Build(Reencode());

        Assert.DoesNotContain("-0:t", args);
        Assert.DoesNotContain("-0:d", args);
    }

    [Fact]
    public void Drops_data_streams_for_a_hardware_matroska_re_encode_but_keeps_attachments()
    {
        // A hardware encoder can abort on a data stream even in Matroska, so drop those; the font
        // attachment is fine and stays.
        var args = FfmpegCommandBuilder.Build(Reencode(), videoEncoder: "hevc_qsv");

        Assert.Contains(("-map", "-0:d"), MapPairs(args));
        Assert.DoesNotContain("-0:t", args);
    }

    [Fact]
    public void Regenerates_timestamps_for_a_video_job_before_the_input()
    {
        var args = FfmpegCommandBuilder.Build(Reencode());

        // -fflags +genpts is a demuxer flag: it must precede -i to apply to the source.
        var index = IndexOf(args, "-fflags");
        Assert.True(index >= 0);
        Assert.Equal("+genpts", args[index + 1]);
        Assert.True(index < IndexOf(args, "-i"));
    }

    [Fact]
    public void Regenerates_timestamps_for_a_remux_too_but_not_for_an_audio_job()
    {
        Assert.Contains("+genpts", FfmpegCommandBuilder.Build(Reencode(videoCodec: null)));
        Assert.DoesNotContain("-fflags", FfmpegCommandBuilder.Build(AudioReencode()));
    }

    [Fact]
    public void Drops_attachment_and_data_streams_for_an_mp4_remux()
    {
        // A remux from .mkv to .mp4 hits the same wall, so the exclusion must apply there too.
        var args = FfmpegCommandBuilder.Build(
            Reencode(videoCodec: null) with { OutputPath = "/work/Movie.opt.mp4" });

        Assert.Contains(("-map", "-0:t"), MapPairs(args));
        Assert.Contains(("-map", "-0:d"), MapPairs(args));
    }

    // Pairs each "-map" with the argument that follows it, so a negative-mapping exclusion can be
    // asserted unambiguously (there are several "-map" arguments in a command).
    private static IEnumerable<(string, string)> MapPairs(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == "-map")
            {
                yield return (args[i], args[i + 1]);
            }
        }
    }

    [Theory]
    [InlineData(".mp4")]
    [InlineData(".mkv")]
    public void Preserves_variable_timing_when_the_source_was_identified_as_vfr(string extension)
    {
        var args = FfmpegCommandBuilder.Build(Reencode() with
        {
            OutputPath = $"/work/Movie.opt{extension}",
            SourceIsVariableFrameRate = true
        });

        var index = IndexOf(args, "-fps_mode");
        Assert.Equal("vfr", args[index + 1]);
        Assert.Equal("demux", args[IndexOf(args, "-enc_time_base:v:0") + 1]);
    }

    [Fact]
    public void Does_not_retime_a_cfr_or_unknown_source()
    {
        Assert.DoesNotContain("-fps_mode",
            FfmpegCommandBuilder.Build(Reencode() with { OutputPath = "/work/Movie.opt.mp4" }));
        Assert.DoesNotContain("-enc_time_base:v:0", FfmpegCommandBuilder.Build(Reencode()));
    }

    [Fact]
    public void Does_not_force_cfr_on_a_remux_only_job()
    {
        // A remux copies the video stream untouched, so frame timing is never rewritten.
        Assert.DoesNotContain("-fps_mode",
            FfmpegCommandBuilder.Build(Reencode(videoCodec: null) with
            {
                OutputPath = "/work/Movie.opt.mp4",
                SourceIsVariableFrameRate = true
            }));
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

        var vfIndex = IndexOf(args, "-filter:v:0");
        Assert.True(vfIndex >= 0);
        Assert.Contains("tonemap", args[vfIndex + 1]);
    }

    [Fact]
    public void Does_not_add_a_tonemap_filter_otherwise()
    {
        var args = FfmpegCommandBuilder.Build(Reencode(tonemap: false));

        Assert.DoesNotContain("-filter:v:0", args);
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

    [Theory]
    [InlineData(".m4a")]
    [InlineData(".m4b")]
    public void Adds_the_mp4_flag_so_custom_tags_round_trip_for_mp4_audio_outputs(string extension)
    {
        var spec = AudioReencode() with { OutputPath = $"/work/Track{extension}" };

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
        Assert.Equal("libx265", args[IndexOf(args, "-c:v:0") + 1]);
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
