using Optimisarr.Core.Queue;

namespace Optimisarr.Tests;

/// <summary>
/// The detection pass is a decode-only run over a short window with cropdetect attached and the
/// output discarded. It is cheap — no encoder — so it can sample several windows, and it must be
/// shell-free like every other FFmpeg invocation here.
/// </summary>
public sealed class CropDetectCommandBuilderTests
{
    [Fact]
    public void A_window_is_seeked_bounded_and_discarded()
    {
        var args = CropDetectCommandBuilder.Build("/data/film.mkv", startSeconds: 600, durationSeconds: 40);

        // Input seek before -i so the decode starts near the window rather than reading from zero.
        Assert.True(args.ToList().IndexOf("-ss") < args.ToList().IndexOf("-i"));
        Assert.Equal("600", args[args.ToList().IndexOf("-ss") + 1]);
        Assert.Equal("40", args[args.ToList().IndexOf("-t") + 1]);
        Assert.Equal("/data/film.mkv", args[args.ToList().IndexOf("-i") + 1]);
        // Nothing is written anywhere.
        Assert.Equal("null", args[args.ToList().IndexOf("-f") + 1]);
        Assert.Equal("-", args[^1]);
    }

    [Fact]
    public void The_filter_rounds_to_even_and_reports_the_whole_window()
    {
        var args = CropDetectCommandBuilder.Build("/data/film.mkv", startSeconds: 0, durationSeconds: 40);
        var filter = args[args.ToList().IndexOf("-vf") + 1];

        // round=2 keeps every reported edge on a chroma boundary; reset=0 accumulates over the
        // window instead of restarting per frame, so one dark frame cannot narrow the answer.
        Assert.StartsWith("cropdetect=", filter);
        Assert.Contains("round=2", filter);
        Assert.Contains("reset=0", filter);
    }

    [Fact]
    public void The_path_is_passed_verbatim_as_one_argument()
    {
        // Paths are untrusted input. An argument array, never a shell string, is how they stay
        // inert whatever they contain.
        var hostile = "/data/films/it's; rm -rf $HOME.mkv";

        var args = CropDetectCommandBuilder.Build(hostile, 0, 40);

        Assert.Contains(hostile, args);
    }

    [Fact]
    public void A_whole_file_window_omits_the_duration_and_reads_to_the_end()
    {
        // Short files plan one window covering everything; there is no duration to bound.
        var args = CropDetectCommandBuilder.Build("/data/short.mkv", startSeconds: 0, durationSeconds: null);

        Assert.DoesNotContain("-t", args);
        Assert.Equal("0", args[args.ToList().IndexOf("-ss") + 1]);
    }
}
