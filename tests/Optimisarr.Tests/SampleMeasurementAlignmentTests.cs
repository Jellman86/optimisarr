using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

/// <summary>
/// Where the cadence filter goes relative to the window cut.
///
/// <para>Measured on a real episode: a 40-second sample encoded at the library's quality scored a
/// harmonic mean of 6.45 with 128 frames under 5, while the same clip compared against an
/// identically cut reference scored 95.52 with none. The difference was entirely this ordering.
/// `fps` resamples onto a fixed grid and a source whose timestamps are not perfectly regular gains
/// duplicated frames as it does so, which moves which source frames the window then holds — while
/// the clip on the other side was cut by a plain seek that moved nothing. Reordering brought the
/// measurement to 95.52, frame for frame equal to the identically cut comparison.</para>
///
/// <para>Every search therefore reported that every candidate missed the gate, and fell back to the
/// library's own quality having learned nothing, on both the server and a worker.</para>
/// </summary>
public sealed class SampleMeasurementAlignmentTests
{
    private static QualityMeasurementContext Sample(bool cutClip) => new(
        ReferenceWidth: 1440,
        ReferenceHeight: 1080,
        ReferenceIsHdr: false,
        HdrConvertedToSdr: false,
        ReferenceStartSeconds: 121,
        ReferenceDurationSeconds: 1415.04,
        DistortedStartSeconds: null,
        MeasureDurationSeconds: 40,
        ReferenceFrameRate: 24000.0 / 1001.0,
        ReferenceContainerLeadSeconds: 0,
        DistortedIsCutClip: cutClip);

    private static string ReferenceBranch(QualityMeasurementContext context) =>
        QualityScoreCommandBuilder
            .Build("distorted.mkv", "reference.mkv", "log.json", context, threads: 8)
            .FilterGraph
            .Split("[1:v]")[1]
            .Split("[ref]")[0];

    [Fact]
    public void A_clip_has_its_reference_window_cut_before_the_cadence_is_normalised()
    {
        var branch = ReferenceBranch(Sample(cutClip: true));

        Assert.Contains("trim=", branch);
        Assert.Contains("fps=", branch);
        Assert.True(
            branch.IndexOf("trim=", StringComparison.Ordinal) < branch.IndexOf("fps=", StringComparison.Ordinal),
            $"The window must be cut before the cadence is normalised, but the graph was: {branch}");
    }

    [Fact]
    public void A_whole_file_candidate_keeps_the_ordering_it_was_tuned_with()
    {
        // Both of its streams are seeked and trimmed alike, so whatever the cadence filter does to
        // one it does to the other; the half-frame rounding that ordering was chosen for is a real
        // problem there and is not disturbed here.
        var branch = ReferenceBranch(Sample(cutClip: false));

        Assert.True(
            branch.IndexOf("fps=", StringComparison.Ordinal) < branch.IndexOf("trim=", StringComparison.Ordinal),
            $"A whole-file measurement should still normalise the cadence first, but the graph was: {branch}");
    }

    [Fact]
    public void The_clip_itself_is_still_brought_to_the_reference_cadence()
    {
        // Reordering must not lose the cadence normalisation: a frame-rate-capped encode relies on
        // both streams arriving at the same rate before they are compared.
        var graph = QualityScoreCommandBuilder
            .Build("distorted.mkv", "reference.mkv", "log.json", Sample(cutClip: true), threads: 8)
            .FilterGraph;

        var distorted = graph.Split("[0:v]")[1].Split("[dist]")[0];
        Assert.Contains("fps=fps=23.976", distorted);
        Assert.Contains("fps=fps=23.976", ReferenceBranch(Sample(cutClip: true)));
    }

    [Fact]
    public void A_source_with_no_known_frame_rate_still_produces_a_filter_ffmpeg_can_parse()
    {
        // The cadence is dropped when the probe reported no frame rate, and appending it anyway
        // left a trailing comma — an empty element once the next filter was joined on. FFmpeg
        // answers "No such filter: ''" and refuses the graph, so every search on such a source
        // failed at its first scoring pass. Seen in a real lease as "STARTPTS,,scale".
        var context = Sample(cutClip: true) with { ReferenceFrameRate = null };

        var graph = QualityScoreCommandBuilder
            .Build("distorted.mkv", "reference.mkv", "log.json", context, threads: 8)
            .FilterGraph;

        Assert.DoesNotContain(",,", graph, StringComparison.Ordinal);
        Assert.Contains("trim=", graph, StringComparison.Ordinal);
    }

    [Fact]
    public void A_clip_with_no_known_frame_rate_is_paired_frame_by_frame()
    {
        // libvmaf pairs each frame with the latest frame of the other stream at or before its
        // timestamp. A sample encoded with exact 1001/24000 steps against a source stored in
        // milliseconds sits up to 0.4 ms behind it, so without a common grid a third of the frames
        // met their predecessor: a clean libx265 sample scored harmonic 59.8 with zeros on every
        // cut, and 94.9 once paired frame by frame. Both are cut clips, so order is the pairing.
        var context = Sample(cutClip: true) with { ReferenceFrameRate = null };

        var branches = QualityScoreCommandBuilder
            .Build("distorted.mkv", "reference.mkv", "log.json", context, threads: 8)
            .FilterGraph
            .Split(';');

        Assert.EndsWith("setpts=PTS-STARTPTS,setpts=N,scale=1440:1080", branches[0].Split(":flags")[0]);
        Assert.EndsWith("setpts=PTS-STARTPTS,setpts=N,scale=1440:1080", branches[1].Split(":flags")[0]);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void No_branch_of_any_graph_ever_carries_an_empty_element(bool cutClip, bool knownFrameRate)
    {
        // The general form of the same mistake. Every one of these pieces is optional, and each is
        // glued to the next with a comma, so any of them being absent can leave one too many.
        var context = Sample(cutClip) with
        {
            ReferenceFrameRate = knownFrameRate ? 24000.0 / 1001.0 : null,
        };

        foreach (var branch in QualityScoreCommandBuilder
                     .Build("distorted.mkv", "reference.mkv", "log.json", context, threads: 8)
                     .FilterGraph
                     .Split(';'))
        {
            Assert.DoesNotContain(",,", branch, StringComparison.Ordinal);
            Assert.DoesNotContain(",[", branch, StringComparison.Ordinal);
            Assert.False(branch.TrimEnd(']').EndsWith(',') , $"branch ends with a comma: {branch}");
        }
    }

    [Fact]
    public void Nothing_changes_for_a_measurement_with_no_window_to_cut()
    {
        // A whole-file comparison has no trim at all, so there is no ordering to get wrong and the
        // flag must not introduce one.
        var whole = new QualityMeasurementContext(
            ReferenceWidth: 1440, ReferenceHeight: 1080,
            ReferenceIsHdr: false, HdrConvertedToSdr: false,
            ReferenceFrameRate: 24000.0 / 1001.0);

        var with = QualityScoreCommandBuilder.Build("d.mkv", "r.mkv", "l.json", whole, 8).FilterGraph;
        var without = QualityScoreCommandBuilder
            .Build("d.mkv", "r.mkv", "l.json", whole with { DistortedIsCutClip = true }, 8).FilterGraph;

        Assert.Equal(with, without);
    }
}
