using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

/// <summary>
/// Choosing where the candidate's pictures sit against the reference's.
///
/// <para>The shift this replaces was derived from the containers' headers. The pair that exposed
/// it are identical in every header field and need opposite answers, so the number had to be
/// measured. What is pinned here is the shape of the probe and how the answer is written; the
/// choosing itself is proved against real files by the macOS sidecar's live alignment tests.</para>
/// </summary>
public sealed class TimelineAlignmentProbeTests
{
    private static string Log(params double[] scores) =>
        "{\"frames\":["
        + string.Join(",", scores.Select(s =>
            "{\"metrics\":{\"vmaf\":" + s.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}}"))
        + "]}";

    [Fact]
    public void A_probe_is_scored_by_its_mean()
    {
        // The mean, not the harmonic mean: this chooses between alignments rather than judging
        // quality, and a harmonic mean collapses to near zero for every offset once any of them
        // contains a zero — which tells them apart far less clearly.
        Assert.Equal(50, TimelineAlignmentProbe.MeanScore(Log(0, 100))!.Value);
        Assert.Equal(90, TimelineAlignmentProbe.MeanScore(Log(88, 90, 92))!.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"frames\":[]}")]
    [InlineData("{\"pooled_metrics\":{\"vmaf\":{\"mean\":90}}}")]
    public void A_probe_that_scored_nothing_is_not_a_score_of_zero(string json)
    {
        // It must not win by being the lowest number. An unscoreable offset is no evidence at all.
        Assert.Null(TimelineAlignmentProbe.MeanScore(json));
    }

    [Fact]
    public void The_probe_moves_the_candidate_and_never_the_reference()
    {
        // The real measurement shifts the distorted input only, so the probe has to as well — an
        // alignment chosen against a differently-built comparison is the alignment for a
        // measurement nobody runs.
        var arguments = TimelineAlignmentProbe.Arguments("ref.mkv", "dist.mp4", 0.04, "log.json");
        var graph = arguments[arguments.ToList().IndexOf("-lavfi") + 1];

        Assert.Contains("[0:v]settb=AVTB,setpts=PTS-40000.000000", graph);
        Assert.Contains("[1:v]settb=AVTB,trim=start=1", graph);
        Assert.Equal("dist.mp4", arguments[arguments.ToList().IndexOf("-i") + 1]);
    }

    [Fact]
    public void Sample_alignment_probe_seeks_to_its_own_window()
    {
        var arguments = TimelineAlignmentProbe.Arguments("ref.mkv", "dist.mp4", 0, "log.json", 267);

        Assert.Equal("267", arguments[Array.IndexOf(arguments.ToArray(), "-ss") + 1]);
    }

    [Fact]
    public void The_chosen_offset_is_written_the_way_the_builder_writes_seconds()
    {
        Assert.Equal("0", TimelineAlignmentProbe.Format(0));
        Assert.Equal("0.04", TimelineAlignmentProbe.Format(0.04));
        Assert.Equal("-0.04", TimelineAlignmentProbe.Format(-0.04));
        // Below the resolution the filtergraph works in, so it is no shift rather than a long one.
        Assert.Equal("0", TimelineAlignmentProbe.Format(0.0000001));
    }

    [Fact]
    public void A_frame_length_needs_a_usable_rate()
    {
        Assert.Equal(0.04, TimelineAlignmentProbe.FrameSeconds(25)!.Value, 6);
        Assert.Null(TimelineAlignmentProbe.FrameSeconds(0));
        Assert.Null(TimelineAlignmentProbe.FrameSeconds(null));
    }

    [Fact]
    public void Every_machine_tries_the_same_offsets()
    {
        // A server that probed differently from its workers would disagree with them about the
        // same pair of files.
        Assert.Equal([0, 1, -1], TimelineAlignmentProbe.FramesToTry);
    }
}
