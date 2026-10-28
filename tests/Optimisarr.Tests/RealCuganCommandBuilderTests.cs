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
            "-m", "/models/models-se",
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
        Assert.Equal("/opt/models/models-pro", After(args, "-m"));
    }

    [Fact]
    public void Frame_extraction_writes_a_zero_based_png_sequence_at_source_timing()
    {
        var args = RealCuganCommandBuilder.BuildFrameExtraction(
            "/media/source.mkv", "/work/1/frames-in", 23.976, 4);

        var output = args[^1];
        Assert.EndsWith("f%08d.png", output);
        Assert.Contains("/work/1/frames-in", output);
        Assert.Equal(new[] { "-start_number", "0" }, Pair(args, "-start_number"));
        Assert.Equal(new[] { "-fps_mode", "passthrough" }, Pair(args, "-fps_mode"));
        // No filters: the engine owns the picture work.
        Assert.DoesNotContain("-vf", args);
        Assert.DoesNotContain("-filter", args);
    }

    [Fact]
    public void Reassembly_encodes_the_frames_and_copies_the_source_streams()
    {
        var spec = new TranscodeSpec(
            "/media/source.mkv", "/work/1/candidate.mkv",
            VideoCodec: "hevc", Crf: 20, Preset: "slow", TonemapToSdr: false);
        var args = RealCuganCommandBuilder.BuildReassembly(
            spec, resolvedEncoder: "libx265", "/work/1/frames-out", 23.976,
            threads: 4, optimisedMarker: "optimisarr-1");

        Assert.Equal(new[] { "-framerate", "23.976" }, Pair(args, "-framerate"));
        Assert.EndsWith("f%08d.png", After(args, "-i"));
        // The original source rides along as the second input for audio, subtitles and attachments.
        Assert.Equal("/media/source.mkv", args[IndexOf(args, "-i", IndexOf(args, "-i") + 1) + 1]);
        Assert.Equal(new[] { "-c:v:0", "libx265" }, Pair(args, "-c:v:0"));
        Assert.Equal(new[] { "-crf", "20" }, Pair(args, "-crf"));
        Assert.Equal(new[] { "-preset", "slow" }, Pair(args, "-preset"));
        // RGB PNG frames must not become an RGB/4:4:4 HEVC stream.
        Assert.Equal(new[] { "-pix_fmt", "yuv420p" }, Pair(args, "-pix_fmt"));
        Assert.Equal(new[] { "-c:a", "copy" }, Pair(args, "-c:a"));
        // Subtitles in a Matroska output are copied verbatim.
        Assert.Equal(new[] { "-c:s", "copy" }, Pair(args, "-c:s"));
        Assert.Equal(new[] { "-metadata", "optimisarr=optimisarr-1" }, Pair(args, "-metadata"));
        Assert.Equal("/work/1/candidate.mkv", args[^1]);
    }

    [Fact]
    public void Reassembly_honours_kept_language_removals_relative_to_the_source()
    {
        var spec = new TranscodeSpec(
            "/media/source.mkv", "/work/1/candidate.mkv",
            VideoCodec: "hevc", Crf: 20, Preset: "slow", TonemapToSdr: false,
            RemoveAudioStreamIndexes: [1], RemoveSubtitleStreamIndexes: [0]);
        var args = RealCuganCommandBuilder.BuildReassembly(
            spec, "libx265", "/work/1/frames-out", 23.976, threads: 0, optimisedMarker: null);

        // The removals reference the SOURCE input (index 1 here), not the frame sequence.
        Assert.Contains("-1:a:1", args);
        Assert.Contains("-1:s:0", args);
    }

    [Fact]
    public void Reassembly_into_mp4_uses_mov_text_and_drops_attachments_and_data()
    {
        var spec = new TranscodeSpec(
            "/media/source.mp4", "/work/1/candidate.mp4",
            VideoCodec: "hevc", Crf: 20, Preset: "slow", TonemapToSdr: false);
        var args = RealCuganCommandBuilder.BuildReassembly(
            spec, "libx265", "/work/1/frames-out", 23.976, threads: 0, optimisedMarker: null);

        Assert.Equal(new[] { "-c:s", "mov_text" }, Pair(args, "-c:s"));
        // Attachments and data are whitelist-excluded: they are never mapped at all.
        Assert.DoesNotContain("1:t", args);
        Assert.DoesNotContain("1:d", args);
    }

    [Fact]
    public void Reassembly_of_a_video_only_spec_does_not_map_source_streams()
    {
        var spec = new TranscodeSpec(
            "/media/source.mkv", "/work/1/candidate.mkv",
            VideoCodec: "hevc", Crf: 20, Preset: "slow", TonemapToSdr: false,
            VideoOnly: true);
        var args = RealCuganCommandBuilder.BuildReassembly(
            spec, "libx265", "/work/1/frames-out", 23.976, threads: 0, optimisedMarker: null);

        Assert.DoesNotContain("-c:a", args);
        Assert.DoesNotContain("-c:s", args);
        // Only the frame sequence is an input, and only its video stream is read.
        Assert.Equal(1, args.Count(a => a == "-i"));
        // With nothing copied from the source there is no metadata to point at.
        Assert.DoesNotContain("-map_metadata", args);
    }

    [Fact]
    public void Reassembly_carries_global_metadata_and_chapters_from_the_source()
    {
        var spec = new TranscodeSpec(
            "/media/source.mkv", "/work/1/candidate.mkv",
            VideoCodec: "hevc", Crf: 20, Preset: "slow", TonemapToSdr: false);
        var args = RealCuganCommandBuilder.BuildReassembly(
            spec, "libx265", "/work/1/frames-out", 23.976, threads: 0, optimisedMarker: null);

        // The frame sequence (input 0) carries no global metadata; the source (input 1) does.
        Assert.Equal(new[] { "-map_metadata", "1" }, Pair(args, "-map_metadata"));
        Assert.Equal(new[] { "-map_chapters", "1" }, Pair(args, "-map_chapters"));
    }
}
