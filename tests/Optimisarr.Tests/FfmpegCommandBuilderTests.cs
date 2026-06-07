using Optimisarr.Core.Queue;

namespace Optimisarr.Tests;

public sealed class FfmpegCommandBuilderTests
{
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
}
