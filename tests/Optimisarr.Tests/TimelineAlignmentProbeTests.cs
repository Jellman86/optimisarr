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
    public void A_probe_is_the_real_measurement_cut_short()
    {
        // The old probe built its own graph: no cadence grid, no lead correction, its own seek.
        // "+1 frame is best" there meant "one frame off" in the measurement that followed, and a
        // clean libx265 encode that scores 95 at shift 0 was failed at harmonic 9. Only the
        // measurement's own graph can say which shift that graph needs.
        string[] measurement = ["-ss", "2415.017333", "-i", "cand.mp4", "-ss", "2415.017333",
            "-i", "src.mkv", "-lavfi", "[0:v]setpts=PTS-0.041708*1000000,trim=start=4.982667:duration=40[dist]",
            "-t", "40", "-f", "null", "-"];

        var probe = TimelineAlignmentProbe.Truncate(measurement, TimelineAlignmentProbe.ProbeSeconds);

        Assert.Equal(measurement.Length, probe.Count);
        Assert.Equal(measurement[..^5], probe.Take(measurement.Length - 5));
        Assert.Equal(["-t", "5", "-f", "null", "-"], probe.TakeLast(5));
    }

    [Fact]
    public void A_full_file_measurement_is_cut_short_before_its_output()
    {
        string[] measurement = ["-i", "cand.mp4", "-i", "src.mkv", "-lavfi", "graph", "-f", "null", "-"];

        Assert.Equal(
            ["-i", "cand.mp4", "-i", "src.mkv", "-lavfi", "graph", "-t", "5", "-f", "null", "-"],
            TimelineAlignmentProbe.Truncate(measurement, 5));
    }

    [Fact]
    public void A_clearly_better_shift_wins()
    {
        // A frame off in the real graph costs tens of points on any motion or cut.
        Assert.Equal(0, TimelineAlignmentProbe.Choose([(0, 96.2), (1, 19.7), (-1, 20.1)]));
        Assert.Equal(1, TimelineAlignmentProbe.Choose([(0, 0.2), (1, 92.3), (-1, 0.3)]));
        Assert.Equal(-1, TimelineAlignmentProbe.Choose([(1, 40), (-1, 91)]));
    }

    [Fact]
    public void A_near_tie_keeps_the_unshifted_timeline()
    {
        // A static shot scores the same at every shift. Moving the whole window on noise from a
        // few seconds of dialogue is how a good encode gets failed.
        Assert.Equal(0, TimelineAlignmentProbe.Choose([(0, 95.1), (1, 96.4), (-1, 94.8)]));
    }

    [Fact]
    public void Nothing_scored_chooses_nothing()
    {
        Assert.Null(TimelineAlignmentProbe.Choose([]));
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
