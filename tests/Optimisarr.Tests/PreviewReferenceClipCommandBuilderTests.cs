using Optimisarr.Api.Queue;

namespace Optimisarr.Tests;

public sealed class PreviewReferenceClipCommandBuilderTests
{
    [Fact]
    public void Builds_a_middle_reference_clip_with_seek_before_input_and_duration_before_output()
    {
        var args = PreviewReferenceClipCommandBuilder.Build(
            "/data/films/Movie.mkv",
            "/work/preview/42/.optimisarr-preview-reference.mkv",
            60,
            1800);

        Assert.Equal("-y", args[0]);
        Assert.Equal("-ss", args[1]);
        Assert.Equal("1800", args[2]);
        Assert.True(IndexOf(args, "-ss") < IndexOf(args, "-i"));
        Assert.Equal("/data/films/Movie.mkv", args[IndexOf(args, "-i") + 1]);
        Assert.Equal("60", args[IndexOf(args, "-t") + 1]);
        Assert.True(IndexOf(args, "-t") < args.Count - 1);
        Assert.Equal("0", args[IndexOf(args, "-map") + 1]);
        Assert.Equal("copy", args[IndexOf(args, "-c") + 1]);
        Assert.Equal("make_zero", args[IndexOf(args, "-avoid_negative_ts") + 1]);
        Assert.Equal("/work/preview/42/.optimisarr-preview-reference.mkv", args[^1]);
    }

    [Fact]
    public void Omits_seek_when_the_source_is_shorter_than_the_preview_window()
    {
        var args = PreviewReferenceClipCommandBuilder.Build(
            "/data/short.mkv",
            "/work/preview/43/.optimisarr-preview-reference.mkv",
            60,
            null);

        Assert.DoesNotContain("-ss", args);
        Assert.Equal("60", args[IndexOf(args, "-t") + 1]);
    }

    [Fact]
    public void Audio_reference_is_a_single_lossless_flac_stream_for_native_browser_playback()
    {
        var args = PreviewReferenceClipCommandBuilder.BuildAudio(
            "/data/music/track.wav",
            "/work/calibration/reference.flac",
            15,
            30);

        Assert.Equal("0:a:0", args[IndexOf(args, "-map") + 1]);
        Assert.Equal("flac", args[IndexOf(args, "-c:a") + 1]);
        Assert.Equal("15", args[IndexOf(args, "-t") + 1]);
        Assert.Equal("/work/calibration/reference.flac", args[^1]);
    }

    private static int IndexOf(IReadOnlyList<string> args, string value) =>
        ((List<string>)args).IndexOf(value);
}
