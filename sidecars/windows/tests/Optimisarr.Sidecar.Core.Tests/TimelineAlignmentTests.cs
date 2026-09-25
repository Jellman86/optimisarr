using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Core.Tests;

/// <summary>
/// Choosing where the candidate's pictures sit against the source's.
///
/// <para>The arithmetic this replaced answered zero for every file either sidecar ever measured,
/// because it read the video stream's start less the container's and those are equal in every real
/// container. The pair that exposed it are identical in every header field and need opposite
/// answers, so what is pinned here is the shape of the probe and the sign of what comes out — the
/// choosing itself is proved against real files in the macOS suite's live alignment tests.</para>
/// </summary>
public sealed class TimelineAlignmentTests
{
    private static string Log(params double[] scores) =>
        "{\"frames\":["
        + string.Join(",", scores.Select(s =>
            "{\"metrics\":{\"vmaf\":" + s.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}}"))
        + "]}";

    [Fact]
    public void A_probe_is_scored_by_its_mean()
    {
        // The mean, not the harmonic mean: this is choosing between alignments rather than judging
        // quality, and a harmonic mean collapses to nearly zero for every candidate offset once
        // any of them contains a zero, which tells them apart far less clearly.
        Assert.Equal(50, VmafMean(Log(0, 100)));
        Assert.Equal(90, VmafMean(Log(88, 90, 92)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"frames\":[]}")]
    [InlineData("{\"pooled_metrics\":{\"vmaf\":{\"mean\":90}}}")]
    public void A_probe_that_scored_nothing_is_not_a_score_of_zero(string json)
    {
        // It must not win by being the lowest number. An unscoreable offset is no evidence at all.
        Assert.Null(TimelineAlignment.MeanScore(json));
    }

    [Fact]
    public void The_frame_length_comes_from_the_sources_own_rate()
    {
        Assert.Equal(0.04, TimelineAlignment.FrameSeconds("25/1")!.Value, 6);
        Assert.Equal(1 / 23.976, TimelineAlignment.FrameSeconds("24000/1001")!.Value, 6);
    }

    [Fact]
    public void Frame_rate_probe_ignores_attached_artwork()
    {
        var arguments = TimelineAlignment.FrameRateArguments("source.mkv");
        Assert.Equal("V:0", arguments[Array.IndexOf(arguments.ToArray(), "-select_streams") + 1]);
    }

    [Theory]
    [InlineData("0/0")]
    [InlineData("25")]
    [InlineData("")]
    [InlineData("N/A")]
    public void A_rate_that_cannot_be_read_is_not_invented(string probeOutput)
    {
        Assert.Null(TimelineAlignment.FrameSeconds(probeOutput));
    }

    [Fact]
    public void A_probe_is_the_real_measurement_cut_short()
    {
        string[] command = ["-ss", "263.01275", "-i", "cand.mp4", "-lavfi", "graph", "-t", "40", "-f", "null", "-"];

        Assert.Equal(["-ss", "263.01275", "-i", "cand.mp4", "-lavfi", "graph", "-t", "5", "-f", "null", "-"],
            TimelineAlignment.Truncate(command, TimelineAlignment.ProbeSeconds));
        // A full-file measurement has no limit of its own, so one is added before the output.
        Assert.Equal(["-i", "cand.mp4", "-lavfi", "graph", "-t", "5", "-f", "null", "-"],
            TimelineAlignment.Truncate(["-i", "cand.mp4", "-lavfi", "graph", "-f", "null", "-"], 5));
    }

    [Fact]
    public void A_clearly_better_offset_wins_and_a_near_tie_keeps_the_unshifted_timeline()
    {
        Assert.Equal(0, TimelineAlignment.Choose([(0, 96.2), (1, 19.7), (-1, 20.1)]));
        Assert.Equal(1, TimelineAlignment.Choose([(0, 0.2), (1, 92.3), (-1, 0.3)]));
        // A static shot scores alike at every shift; noise must not move the whole window.
        Assert.Equal(0, TimelineAlignment.Choose([(0, 95.1), (1, 96.4), (-1, 94.8)]));
        Assert.Null(TimelineAlignment.Choose([]));
    }

    [Fact]
    public void Both_sidecars_try_the_same_offsets()
    {
        // Two workers that aligned differently would be reporting measurements of different things.
        Assert.Equal([0, 1, -1], TimelineAlignment.FramesToTry);
        Assert.Equal(5, TimelineAlignment.ProbeSeconds);
        Assert.Equal(2, TimelineAlignment.ChoiceMarginPoints);
    }

    private static double VmafMean(string json) => TimelineAlignment.MeanScore(json)!.Value;
}
