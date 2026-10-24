using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

public sealed class WindowAlignmentTests
{
    private static IReadOnlyList<AlignmentScore> Scores(params double[] blocks) => blocks
        .SelectMany((score, block) => Enumerable.Range(0, 50)
            .Select(frame => new AlignmentScore(block * 250 + frame * 5, score))).ToArray();

    [Fact]
    public void Static_opening_does_not_hide_a_clear_later_frame_offset()
    {
        Assert.Equal(1, TimelineAlignmentProbe.ChooseWindow(
            [(0, Scores(100, 100, 40, 40)), (1, Scores(100, 100, 98, 98)), (-1, Scores(100, 100, 20, 20))], 50, 20));
    }

    [Fact]
    public void Opposing_offsets_in_different_scenes_cannot_be_combined_into_a_passing_alignment()
    {
        Assert.Equal(0, TimelineAlignmentProbe.ChooseWindow(
            [(0, Scores(98, 98, 30, 30)), (1, Scores(30, 30, 98, 98)), (-1, Scores(20, 20, 20, 20))], 50, 20));
    }

    [Fact]
    public void A_static_or_nearly_tied_window_keeps_zero()
    {
        Assert.Equal(0, TimelineAlignmentProbe.ChooseWindow(
            [(0, Scores(96, 96)), (1, Scores(97, 97)), (-1, Scores(95, 95))], 50, 10));
    }

    [Fact]
    public void A_missing_baseline_cannot_authorise_a_correction()
    {
        Assert.Null(TimelineAlignmentProbe.ChooseWindow([(1, Scores(98, 98)), (-1, Scores(20, 20))], 50, 10));
    }

    [Fact]
    public void Incomplete_or_differently_sampled_evidence_keeps_the_baseline()
    {
        Assert.Equal(0, TimelineAlignmentProbe.ChooseWindow(
            [(0, Scores(30, 30)), (1, Scores(98)), (-1, Scores(20, 20))], 50, 10));
        Assert.Equal(0, TimelineAlignmentProbe.ChooseWindow(
            [(0, Scores(30)), (1, Scores(98)), (-1, Scores(20))], 50, 40));
    }

    [Fact]
    public void An_already_aligned_window_is_not_moved()
    {
        Assert.Equal(0, TimelineAlignmentProbe.ChooseWindow(
            [(0, Scores(98, 98)), (1, Scores(20, 20)), (-1, Scores(30, 30))], 50, 10));
    }

    [Fact]
    public void Probe_covers_the_bounded_window_without_changing_final_measurement_sampling()
    {
        string[] command = ["-i", "candidate", "-lavfi", "libvmaf=n_subsample=1:log_fmt=json", "-t", "40", "-f", "null", "-"];
        var probe = TimelineAlignmentProbe.Prepare(command);
        Assert.Contains("libvmaf=n_subsample=5:log_fmt=json", probe);
        Assert.Equal("40", probe[Array.IndexOf(probe.ToArray(), "-t") + 1]);
        Assert.Contains("libvmaf=n_subsample=1:log_fmt=json", command);
        Assert.Equal(40, TimelineAlignmentProbe.Duration(command));
        Assert.Equal(5, TimelineAlignmentProbe.Duration(["-f", "null", "-"]));
        Assert.Equal(40, TimelineAlignmentProbe.Duration(["-t", "3600", "-f", "null", "-"]));
    }

    [Theory]
    [InlineData("{\"frames\":[{\"frameNum\":-1,\"metrics\":{\"vmaf\":98}}]}")]
    [InlineData("{\"frames\":[{\"frameNum\":0,\"metrics\":{\"vmaf\":98}},{\"frameNum\":0,\"metrics\":{\"vmaf\":99}}]}")]
    [InlineData("{\"frames\":[{\"metrics\":{\"vmaf\":98}}]}")]
    public void Invalid_frame_identity_is_not_alignment_evidence(string json)
    {
        Assert.Null(TimelineAlignmentProbe.ReadScores(json));
    }

    [Theory]
    [InlineData("fps=fps=23.976023976023978:start_time=0", 0.041708)]
    [InlineData("trim=start_frame=round({{distortedShift}}*50)", 0.02)]
    public void Worker_frame_duration_uses_the_frozen_measurement_cadence(string graph, double seconds)
    {
        Assert.Equal(seconds, TimelineAlignmentProbe.CommandFrameSeconds(["-lavfi", graph])!.Value, 6);
    }

    [Theory]
    [InlineData("fps=fps=0:start_time=0")]
    [InlineData("fps=fps=1e309:start_time=0")]
    [InlineData("graph")]
    public void Unusable_command_cadence_keeps_the_probed_fallback(string graph)
    {
        Assert.Null(TimelineAlignmentProbe.CommandFrameSeconds(["-lavfi", graph]));
    }
}
