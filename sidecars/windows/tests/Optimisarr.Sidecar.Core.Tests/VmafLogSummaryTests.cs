using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Core.Tests;

/// <summary>
/// What one measured window is reduced to for the log.
///
/// <para>A candidate measured on PICARD came back with a mean of 80.6 and a harmonic mean of 37.1,
/// and nothing said which of its three windows had gone wrong. The numbers chosen here are the
/// ones that tell those two cases apart.</para>
/// </summary>
public sealed class VmafLogSummaryTests
{
    /// <summary>A libvmaf log holding exactly these frame scores.</summary>
    private static string Log(params double[] scores) =>
        "{\"frames\":["
        + string.Join(",", scores.Select(score =>
            "{\"metrics\":{\"vmaf\":" + score.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}}"))
        + "]}";

    [Fact]
    public void A_good_window_reads_as_one()
    {
        Assert.Equal("3 frames, mean 90.00, harmonic 89.97, min 88.00, 0 below 5",
            VmafLogSummary.Of(Log(88, 90, 92)));
    }

    [Fact]
    public void A_misaligned_window_is_not_hidden_by_its_average()
    {
        // The whole reason this exists. Three frames of ninety and one of zero average out to a
        // respectable sixty-seven; the harmonic mean and the count below the floor do not.
        var summary = VmafLogSummary.Of(Log(90, 90, 90, 0));

        Assert.Contains("mean 67.50", summary);
        Assert.Contains("harmonic 0.04", summary);
        Assert.Contains("min 0.00", summary);
        Assert.Contains("1 below 5", summary);
    }

    [Fact]
    public void A_zero_scoring_frame_is_a_very_low_number_and_not_a_division_by_zero()
    {
        // Guarded rather than left to arithmetic: an unguarded harmonic mean of a set containing
        // zero is infinity, which would print as a summary nobody can read.
        var summary = VmafLogSummary.Of(Log(0, 0));

        Assert.NotNull(summary);
        Assert.DoesNotContain("∞", summary);
        Assert.DoesNotContain("NaN", summary);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("""{"frames":[]}""")]
    [InlineData("""{"pooled_metrics":{"vmaf":{"mean":90}}}""")]
    [InlineData("""{"frames":[{"metrics":{}}]}""")]
    public void A_log_with_no_frame_scores_describes_nothing_rather_than_throwing(string json)
    {
        // This only describes a measurement FFmpeg's exit code has already judged, so it must
        // never be the thing that fails a job.
        Assert.Null(VmafLogSummary.Of(json));
    }
}
