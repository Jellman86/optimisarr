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

    [Fact]
    public void Sample_alignment_probes_the_window_it_will_score()
    {
        string[] command = ["-ss", "263.01275", "-i", "{{distorted}}", "-ss", "263.01275",
            "-i", "{{reference}}", "-lavfi", "[0:v]trim=start=4.98725:duration=40[dist]",
            "-t", "40", "-f", "null", "-"];

        Assert.Equal(267, TimelineAlignment.ProbeStartForCommand(command));
    }

    [Fact]
    public void Full_file_alignment_keeps_its_existing_probe_location()
    {
        Assert.Equal(TimelineAlignment.ProbeStartSeconds,
            TimelineAlignment.ProbeStartForCommand(["-i", "{{distorted}}", "-i", "{{reference}}"]));
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
    public void The_probe_moves_the_candidate_and_never_the_source()
    {
        // The server's own measurement shifts the distorted input only, so the probe has to as
        // well — an alignment chosen against a differently-built comparison is the alignment for a
        // measurement nobody runs.
        var arguments = TimelineAlignment.Arguments("src.mkv", "cand.mp4", 0.04, "log.json");
        var graph = arguments[arguments.ToList().IndexOf("-lavfi") + 1];

        Assert.Contains("[0:v]settb=AVTB,setpts=PTS-40000.000000", graph);
        Assert.Contains("[1:v]settb=AVTB,trim=start=1", graph);
        // The candidate is the first input, as it is in the real command.
        Assert.Equal("cand.mp4", arguments[arguments.ToList().IndexOf("-i") + 1]);
    }

    [Fact]
    public void Both_sidecars_try_the_same_offsets()
    {
        // Two workers that aligned differently would be reporting measurements of different things.
        Assert.Equal([0, 1, -1], TimelineAlignment.FramesToTry);
    }

    private static double VmafMean(string json) => TimelineAlignment.MeanScore(json)!.Value;
}
