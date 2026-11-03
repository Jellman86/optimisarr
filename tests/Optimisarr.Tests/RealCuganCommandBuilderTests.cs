using Optimisarr.Core.Queue;
using Optimisarr.Core.SuperResolution;

namespace Optimisarr.Tests;

/// <summary>
/// Every command must be a flat argument array (the safety standard), pass the exact engine
/// flags the realcugan-ncnn-vulkan CLI documents, and keep the reassembly's stream mapping
/// consistent with the normal encode path: video re-encoded, audio and subtitles copied from
/// the source with the kept-languages removals honoured.
/// </summary>
public sealed class RealCuganCommandBuilderTests
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

    [Fact]
    public void Engine_arguments_pass_the_documented_flags_and_nothing_else()
    {
        var args = RealCuganCommandBuilder.BuildEngineArguments(
            "/work/1/in", "/work/1/out", "/models", Options);

        Assert.Equal(new[]
        {
            "-i", "/work/1/in",
            "-o", "/work/1/out",
            "-m", Path.Combine("/models", "models-se"),
            "-s", "2",
            "-n", "1",
            "-f", "png",
        }, args);
    }

    [Fact]
    public void Auto_device_omits_the_gpu_flag_letting_the_engine_choose()
    {
        var args = RealCuganCommandBuilder.BuildEngineArguments(
            "in", "out", "models", Options with { Device = "auto" });
        Assert.DoesNotContain("-g", args);
    }

    [Fact]
    public void Cpu_device_passes_minus_one_which_the_engine_documents_as_cpu()
    {
        var args = RealCuganCommandBuilder.BuildEngineArguments(
            "in", "out", "models", Options with { Device = "cpu" });
        Assert.Equal(new[] { "-g", "-1" }, Pair(args, "-g"));
    }

    [Fact]
    public void A_named_vulkan_device_passes_its_index()
    {
        var args = RealCuganCommandBuilder.BuildEngineArguments(
            "in", "out", "models", Options with { Device = "gpu:2" });
        Assert.Equal(new[] { "-g", "2" }, Pair(args, "-g"));
    }

    [Fact]
    public void The_model_directory_is_the_preset_mapping_under_the_configured_root()
    {
        var args = RealCuganCommandBuilder.BuildEngineArguments(
            "in", "out", "/opt/models", Options with { Preset = "pro" });
        Assert.Equal(Path.Combine("/opt/models", "models-pro"), After(args, "-m"));
    }

    private static TranscodeSpec Spec(string output = "/work/1/candidate.mkv", string input = "/media/source.mkv") =>
        new(input, output, VideoCodec: "hevc", Crf: 20, Preset: "slow", TonemapToSdr: false);

    private static IReadOnlyList<string> Encoder(TranscodeSpec spec, double videoStart = 0, double frameRate = 24000.0 / 1001, int bitDepth = 8) =>
        RealCuganCommandBuilder.BuildEncoder(spec, "libx265", frameRate, threads: 4, optimisedMarker: "optimisarr-1", videoStart, bitDepth);

    [Fact]
    public void The_decoder_streams_the_first_video_streams_frames_as_rgb_png_on_stdout()
    {
        var args = RealCuganCommandBuilder.BuildDecoder("/media/source.mkv", threads: 4, sourceBitDepth: 8);

        Assert.Equal("/media/source.mkv", After(args, "-i"));
        Assert.Equal(new[] { "-map", "0:v:0" }, Pair(args, "-map"));
        Assert.Equal(new[] { "-fps_mode", "passthrough" }, Pair(args, "-fps_mode"));
        Assert.Equal(new[] { "-pix_fmt", "rgb24" }, Pair(args, "-pix_fmt"));
        Assert.Equal(new[] { "-f", "image2pipe" }, Pair(args, "-f"));
        Assert.Equal("-", args[^1]);
        // No filters: the engine owns the picture work.
        Assert.DoesNotContain("-vf", args);
        Assert.DoesNotContain("-filter", args);
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
        Assert.Equal(new[] { "-pix_fmt", "yuv420p" }, Pair(args, "-pix_fmt"));
        Assert.Equal(new[] { "-c:a", "copy" }, Pair(args, "-c:a"));
        Assert.Equal(new[] { "-c:s", "copy" }, Pair(args, "-c:s"));
        Assert.Equal(new[] { "-metadata", "optimisarr=optimisarr-1" }, Pair(args, "-metadata"));
        Assert.Equal("/work/1/candidate.mkv", args[^1]);
    }

    [Fact]
    public void A_ten_bit_source_is_dithered_into_the_engines_8_bit_pictures()
    {
        var args = RealCuganCommandBuilder.BuildDecoder("/media/source.mkv", threads: 4, sourceBitDepth: 10);
        Assert.Contains(args, argument => argument.Contains("sws_dither=ed"));
        Assert.Equal(new[] { "-pix_fmt", "rgb24" }, Pair(args, "-pix_fmt"));
    }

    [Fact]
    public void A_ten_bit_source_is_encoded_back_to_ten_bit()
    {
        Assert.Equal(new[] { "-pix_fmt", "yuv420p10le" }, Pair(Encoder(Spec(), bitDepth: 10), "-pix_fmt"));
        Assert.Equal(new[] { "-pix_fmt", "yuv420p" }, Pair(Encoder(Spec(), bitDepth: 8), "-pix_fmt"));
    }

    [Fact]
    public void The_encoder_keeps_the_exact_frame_rate()
    {
        var args = Encoder(Spec(), frameRate: 24000.0 / 1001);
        Assert.Equal(24000.0 / 1001, double.Parse(After(args, "-framerate"), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void A_matroska_candidate_keeps_the_sources_attachments_such_as_subtitle_fonts()
    {
        var args = Encoder(Spec());
        Assert.Contains("1:t?", args);
        Assert.Equal(new[] { "-c:t", "copy" }, Pair(args, "-c:t"));
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
