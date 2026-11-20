using Optimisarr.Core.Queue;
using Optimisarr.Core.SuperResolution;

namespace Optimisarr.Tests;

/// <summary>
/// Every command must be a flat argument array (the safety standard) and keep the reassembly's stream mapping
/// consistent with the normal encode path: video re-encoded, audio and subtitles copied from
/// the source with the kept-languages removals honoured.
/// </summary>
public sealed class SuperResolutionCommandBuilderTests
{
    private static readonly SuperResolutionOptions Options = SuperResolutionOptions.Defaults;

    private static string After(IReadOnlyList<string> args, string flag) => args[IndexOf(args, flag) + 1];

    private static string[] Pair(IReadOnlyList<string> args, string flag) =>
        [flag, After(args, flag)];

    private static int IndexOf(IReadOnlyList<string> args, string value, int startIndex = 0)
    {
        for (var i = startIndex; i < args.Count; i++)
        {
            if (string.Equals(args[i], value, StringComparison.Ordinal)) return i;
        }
        throw new InvalidOperationException($"'{value}' not found in [{string.Join(", ", args)}]");
    }

    private static TranscodeSpec Spec(string output = "/work/1/candidate.mkv", string input = "/media/source.mkv") =>
        new(input, output, VideoCodec: "hevc", Crf: 20, Preset: "slow", TonemapToSdr: false);

    private static readonly SuperResolutionColour Hd = new("bt709", "tv", "bt709", "bt709", "bt709", RangeTagged: true);

    private static IReadOnlyList<string> Encoder(TranscodeSpec spec, double videoStart = 0, double frameRate = 24000.0 / 1001, int bitDepth = 8,
        SuperResolutionColour? colour = null) =>
        SuperResolutionCommandBuilder.BuildEncoder(spec, "libx265", frameRate, threads: 4, optimisedMarker: "optimisarr-1", videoStart, bitDepth, colour ?? Hd);

    [Fact]
    public void An_anamorphic_source_keeps_its_pixel_shape_on_the_output()
    {
        // The engine writes plain pictures with no pixel shape, so the encoder must restore it or
        // a widescreen DVD would play squashed into 4:5.
        var args = SuperResolutionCommandBuilder.BuildEncoder(Spec(), "libx265", 25, 4, "optimisarr-1", 0, 8, Hd,
            new Optimisarr.Core.Domain.SampleAspectRatio(64, 45));

        Assert.Equal("scale=out_color_matrix=bt709:out_range=tv,setsar=sar=64/45,format=yuv420p", After(args, "-vf"));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(null, null)]
    public void A_square_source_adds_no_pixel_shape_filter(int? numerator, int? denominator)
    {
        Optimisarr.Core.Domain.SampleAspectRatio? aspect = numerator is { } n ? new(n, denominator!.Value) : null;
        var args = SuperResolutionCommandBuilder.BuildEncoder(Spec(), "libx265", 25, 4, "optimisarr-1", 0, 8, Hd, aspect);

        Assert.Equal("scale=out_color_matrix=bt709:out_range=tv,format=yuv420p", After(args, "-vf"));
    }

    [Fact]
    public void Nvenc_keeps_an_anamorphic_pixel_shape_before_its_ten_bit_upload()
    {
        var args = SuperResolutionCommandBuilder.BuildEncoder(Spec() with { Preset = "p5" }, "hevc_nvenc", 25, 4, "optimisarr-1", 0, 10, Hd,
            new Optimisarr.Core.Domain.SampleAspectRatio(16, 15));

        Assert.Equal("scale=out_color_matrix=bt709:out_range=tv,setsar=sar=16/15,format=p010le", After(args, "-vf"));
    }

    [Theory]
    [InlineData("h264_nvenc", 8, "yuv420p")]
    [InlineData("hevc_nvenc", 8, "yuv420p")]
    [InlineData("hevc_nvenc", 10, "p010le")]
    [InlineData("av1_nvenc", 8, "yuv420p")]
    [InlineData("av1_nvenc", 10, "p010le")]
    public void Nvenc_uses_constant_quality_and_uploads_software_frames_without_hardware_decode(string encoder, int bitDepth, string layout)
    {
        var args = SuperResolutionCommandBuilder.BuildEncoder(Spec() with { Preset = "p5" }, encoder,
            25, 4, "optimisarr-1", 0.042, bitDepth, Hd);

        Assert.Equal(encoder, After(args, "-c:v:0"));
        Assert.Equal("vbr", After(args, "-rc"));
        Assert.Equal("20", After(args, "-cq"));
        Assert.Equal("0", After(args, "-b:v"));
        Assert.Equal("p5", After(args, "-preset"));
        Assert.DoesNotContain("-crf", args);
        Assert.DoesNotContain("-hwaccel", args);
        Assert.Equal($"scale=out_color_matrix=bt709:out_range=tv,format={layout}", After(args, "-vf"));
        Assert.Equal("0.042", After(args, "-itsoffset"));
        Assert.Equal("copy", After(args, "-c:a"));
        Assert.Equal("copy", After(args, "-c:s"));
    }

    [Theory]
    [InlineData("tv", "0")]
    [InlineData("pc", "1")]
    public void Nvenc_h264_preserves_an_explicit_range_even_without_other_colour_tags(string range, string fullRangeFlag)
    {
        var colour = new SuperResolutionColour("bt709", range, null, null, null, RangeTagged: true);
        var args = SuperResolutionCommandBuilder.BuildEncoder(Spec(), "h264_nvenc", 25, 4, null, 0, 8, colour);

        Assert.Equal(range, After(args, "-color_range"));
        Assert.Equal($"h264_metadata=video_full_range_flag={fullRangeFlag}", After(args, "-bsf:v:0"));
    }

    [Fact]
    public void Nvenc_honours_the_librarys_bitrate_cap_and_adaptive_quantisation()
    {
        var spec = Spec() with { Tuning = new EncoderTuning(MaxBitrateKbps: 2000, StrongerAdaptiveQuantisation: true) };
        var args = SuperResolutionCommandBuilder.BuildEncoder(spec, "hevc_nvenc", 25, 4, null, 0, 8, Hd);

        Assert.Equal("2000k", After(args, "-maxrate"));
        Assert.Equal("1", After(args, "-spatial-aq"));
    }

    [Fact]
    public void Noise_is_reduced_before_the_engine_sees_the_picture()
    {
        var args = SuperResolutionCommandBuilder.BuildDecoder("/media/source.mkv", threads: 4, sourceBitDepth: 8, Hd, deinterlace: true,
            denoise: NoiseReduction.Strong);

        Assert.StartsWith($"bwdif=mode=send_frame:parity=auto:deint=interlaced,{NoiseReductionFilter.For(NoiseReduction.Strong)},scale=", After(args, "-vf"));
    }

    [Fact]
    public void A_deinterlaced_source_reaches_the_engine_with_its_fields_combined()
    {
        var args = SuperResolutionCommandBuilder.BuildDecoder("/media/source.mkv", threads: 4, sourceBitDepth: 8, Hd, deinterlace: true);

        Assert.StartsWith("bwdif=mode=send_frame:parity=auto:deint=interlaced,scale=", After(args, "-vf"));
    }

    [Fact]
    public void The_decoder_streams_the_first_video_streams_frames_as_rgb_png_on_stdout()
    {
        var args = SuperResolutionCommandBuilder.BuildDecoder("/media/source.mkv", threads: 4, sourceBitDepth: 8, Hd);

        Assert.Equal("/media/source.mkv", After(args, "-i"));
        Assert.Equal(new[] { "-map", "0:v:0" }, Pair(args, "-map"));
        Assert.Equal(new[] { "-fps_mode", "passthrough" }, Pair(args, "-fps_mode"));
        Assert.Equal(new[] { "-pix_fmt", "rgb24" }, Pair(args, "-pix_fmt"));
        Assert.Equal(new[] { "-f", "image2pipe" }, Pair(args, "-f"));
        Assert.Equal("-", args[^1]);
        // The only filter converts with the source's own matrix and range: the engine owns the picture work.
        Assert.Equal("scale=in_color_matrix=bt709:in_range=tv,format=rgb24", After(args, "-vf"));
    }

    [Fact]
    public void The_encoder_reads_upscaled_frames_from_stdin_and_copies_the_source_streams()
    {
        var args = Encoder(Spec());

        Assert.Contains("-y", args);
        Assert.Equal(new[] { "-f", "image2pipe" }, Pair(args, "-f"));
        Assert.Equal("-", After(args, "-i"));
        // The original source rides along as the second input for audio, subtitles and attachments.
        Assert.Equal("/media/source.mkv", args[IndexOf(args, "-i", IndexOf(args, "-i") + 1) + 1]);
        Assert.Equal(new[] { "-c:v:0", "libx265" }, Pair(args, "-c:v:0"));
        Assert.Equal(new[] { "-crf", "20" }, Pair(args, "-crf"));
        Assert.Equal(new[] { "-preset", "slow" }, Pair(args, "-preset"));
        // RGB PNG frames must not become an RGB/4:4:4 HEVC stream.
        Assert.EndsWith("format=yuv420p", After(args, "-vf"));
        Assert.Equal(new[] { "-c:a", "copy" }, Pair(args, "-c:a"));
        Assert.Equal(new[] { "-c:s", "copy" }, Pair(args, "-c:s"));
        Assert.Equal(new[] { "-metadata", "optimisarr=optimisarr-1" }, Pair(args, "-metadata"));
        Assert.Equal("/work/1/candidate.mkv", args[^1]);
    }

    [Fact]
    public void A_ten_bit_source_is_dithered_into_the_engines_8_bit_pictures()
    {
        var args = SuperResolutionCommandBuilder.BuildDecoder("/media/source.mkv", threads: 4, sourceBitDepth: 10, Hd);
        Assert.Equal("scale=in_color_matrix=bt709:in_range=tv:sws_dither=ed,format=rgb24", After(args, "-vf"));
    }

    [Fact]
    public void A_ten_bit_source_is_encoded_back_to_ten_bit()
    {
        Assert.EndsWith("format=yuv420p10le", After(Encoder(Spec(), bitDepth: 10), "-vf"));
        Assert.EndsWith("format=yuv420p", After(Encoder(Spec(), bitDepth: 8), "-vf"));
    }

    [Fact]
    public void The_encoder_converts_back_with_the_sources_matrix_and_keeps_its_colour_tags()
    {
        var args = Encoder(Spec());
        Assert.Equal("scale=out_color_matrix=bt709:out_range=tv,format=yuv420p", After(args, "-vf"));
        Assert.Equal(new[] { "-colorspace", "bt709" }, Pair(args, "-colorspace"));
        Assert.Equal(new[] { "-color_primaries", "bt709" }, Pair(args, "-color_primaries"));
        Assert.Equal(new[] { "-color_trc", "bt709" }, Pair(args, "-color_trc"));
        Assert.Equal(new[] { "-color_range", "tv" }, Pair(args, "-color_range"));
    }

    [Fact]
    public void An_untagged_source_stays_untagged()
    {
        var args = Encoder(Spec(), colour: new SuperResolutionColour("bt709", "tv", null, null, null));
        Assert.DoesNotContain("-colorspace", args);
        Assert.DoesNotContain("-color_primaries", args);
        Assert.DoesNotContain("-color_range", args);
    }

    [Fact]
    public void A_range_tag_is_restored_even_without_primaries_or_matrix_tags()
    {
        var args = Encoder(Spec(), colour: new SuperResolutionColour("bt709", "pc", null, null, null, RangeTagged: true));
        Assert.Equal("pc", After(args, "-color_range"));
        Assert.DoesNotContain("-color_primaries", args);
    }

    [Fact]
    public void The_encoder_keeps_the_exact_frame_rate()
    {
        var args = Encoder(Spec(), frameRate: 24000.0 / 1001);
        Assert.Equal(24000.0 / 1001, double.Parse(After(args, "-framerate"), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void The_upscaled_encode_copies_no_attachments_because_they_unpace_the_audio_from_the_piped_video()
    {
        // FFmpeg 7 stops pacing the source's audio against the slow piped pictures when an
        // attachment is copied alongside, and writes nearly all of it before the first picture.
        var args = Encoder(Spec());

        Assert.DoesNotContain("1:t?", args);
        Assert.DoesNotContain("-c:t", args);
    }

    [Fact]
    public void The_upscaled_encode_can_write_to_a_video_pass_file_instead_of_the_candidate()
    {
        var args = SuperResolutionCommandBuilder.BuildEncoder(Spec(), "libx265", 25, 4, "optimisarr-1", 0, 8, Hd,
            encodeTo: "/work/super-resolution/1-a/video.mkv");

        Assert.Equal("/work/super-resolution/1-a/video.mkv", args[^1]);
        Assert.DoesNotContain("/work/1/candidate.mkv", args);
        Assert.Equal("optimisarr=optimisarr-1", After(args, "-metadata"));
    }

    [Fact]
    public void The_attachments_are_added_back_by_a_copy_of_the_video_pass_beside_the_source()
    {
        var args = SuperResolutionCommandBuilder.BuildAttachmentRemux(
            "/work/super-resolution/1-a/video.mkv", "/media/source.mkv", "/work/1/candidate.mkv");

        Assert.Equal(new[]
        {
            "-hide_banner", "-nostdin", "-y",
            "-i", "/work/super-resolution/1-a/video.mkv", "-i", "/media/source.mkv",
            // Everything, metadata and chapters come from the video pass; only attachments from the source.
            "-map", "0", "-map", "1:t?", "-map_metadata", "0", "-map_chapters", "0",
            "-c", "copy", "/work/1/candidate.mkv",
        }, args);
    }

    [Fact]
    public void The_video_pass_is_written_in_the_attempts_workspace_with_the_candidates_container()
    {
        Assert.Equal(Path.Combine("/work/super-resolution/1-a", "video.mkv"),
            SuperResolutionCommandBuilder.VideoPassPath("/work/super-resolution/1-a", "/work/1/candidate.MKV"));
    }

    [Fact]
    public void An_mp4_candidate_uses_mov_text_and_maps_no_attachments_or_data()
    {
        var args = Encoder(Spec("/work/1/candidate.mp4", "/media/source.mp4"));
        Assert.Equal(new[] { "-c:s", "mov_text" }, Pair(args, "-c:s"));
        Assert.DoesNotContain("1:t?", args);
        Assert.DoesNotContain("1:d", args);
    }

    [Fact]
    public void A_late_starting_picture_is_placed_where_it_started_so_audio_stays_in_sync()
    {
        var args = Encoder(Spec(), videoStart: 0.042);
        var offset = IndexOf(args, "-itsoffset");
        Assert.Equal("0.042", args[offset + 1]);
        // The offset applies to the frame input, which follows it.
        Assert.True(offset < IndexOf(args, "-i"));
        Assert.DoesNotContain("-itsoffset", Encoder(Spec()));
    }

    [Fact]
    public void The_encoder_honours_kept_language_removals_relative_to_the_source()
    {
        var spec = Spec() with { RemoveAudioStreamIndexes = [1], RemoveSubtitleStreamIndexes = [0] };
        var args = Encoder(spec);
        // The removals reference the SOURCE input (index 1 here), not the frame stream.
        Assert.Contains("-1:a:1", args);
        Assert.Contains("-1:s:0", args);
    }

    [Fact]
    public void The_encoder_carries_global_metadata_and_chapters_from_the_source()
    {
        var args = Encoder(Spec());
        Assert.Equal(new[] { "-map_metadata", "1" }, Pair(args, "-map_metadata"));
        Assert.Equal(new[] { "-map_chapters", "1" }, Pair(args, "-map_chapters"));
    }
}
