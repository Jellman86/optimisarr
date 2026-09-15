using Optimisarr.Api.Workers;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

/// <summary>
/// What a rejected candidate leaves behind. "Missed the VMAF target" is a verdict with no
/// evidence: it reads the same whether the samples were far below the gate or a whisker under it,
/// and the same again whether the measurement itself was sound.
/// </summary>
public sealed class AdaptiveProbeReportTests
{
    private static readonly VerificationPolicy Policy = VerificationPolicy.Default with
    {
        QualityGateEnabled = true,
        MinimumVmafHarmonicMean = 85,
        MinimumVmafMin = 70,
        MinimumVmafCatastrophicMin = 40,
    };

    private static QualityScores Scores(double harmonic, double? fifth, double min, int frames) =>
        new(VmafMean: harmonic + 1, VmafHarmonicMean: harmonic, VmafMin: min,
            PsnrYMean: null, SsimMean: null, VmafFifthPercentile: fifth, FrameCount: frames);

    [Fact]
    public void Says_what_was_measured_and_what_it_was_held_to()
    {
        var probe = new AdaptiveQualityProbe(20, MeetsTarget: false, EncodedBytes: 21_809_738,
            Scores(9.75, 0, 0, 2877));

        var described = AdaptiveProbeReport.Describe(probe, Policy);

        // Both halves of every comparison, so the line answers "how far off was it" on its own —
        // and each figure beside the threshold that actually judges it.
        Assert.Contains("harmonic 9.75 of 85", described);
        Assert.Contains("fifth percentile 0 of 70", described);
        Assert.Contains("lowest 0 of 40", described);
        Assert.Contains("2877 frames", described);
    }

    [Fact]
    public void A_probe_with_no_scores_says_so_rather_than_reading_as_zero()
    {
        // Probes recorded before the scores were kept are still read back off a lease, and zero is
        // a score — it would read as a catastrophic failure rather than as an absent record.
        var probe = new AdaptiveQualityProbe(20, MeetsTarget: true, EncodedBytes: 100);

        Assert.Equal("no scores recorded", AdaptiveProbeReport.Describe(probe, Policy));
    }

    [Fact]
    public void An_unmeasured_figure_is_named_rather_than_printed_as_a_number()
    {
        var probe = new AdaptiveQualityProbe(20, MeetsTarget: false, EncodedBytes: 100,
            Scores(80, fifth: null, min: 60, frames: 10) with { FrameCount = null });

        var described = AdaptiveProbeReport.Describe(probe, Policy);

        // With no fifth percentile measured the gate falls back to the lowest frame, so the line
        // shows what was actually compared rather than a blank.
        Assert.Contains("fifth percentile 60 of 70", described);
        Assert.Contains("unmeasured frames", described);
    }

    [Fact]
    public void Each_figure_stands_beside_the_gate_that_judges_it()
    {
        // The three comparisons are not the ones the names suggest: the *fifth percentile* is held
        // to MinimumVmafMin and the lowest frame to the catastrophic floor, which is far lower.
        // Written the other way round, this line printed "lowest 62.07 of 70" beside "met the VMAF
        // target" on a candidate that had passed — a contradiction in the one place someone looks
        // to understand a verdict — and left the number that was actually deciding bare.
        var probe = new AdaptiveQualityProbe(32, MeetsTarget: true, EncodedBytes: 16_385_456,
            Scores(93.14, 78.45, 62.07, 2878));

        var described = AdaptiveProbeReport.Describe(probe, Policy);

        Assert.Contains("fifth percentile 78.45 of 70", described);
        Assert.Contains("lowest 62.07 of 40", described);
        Assert.DoesNotContain("lowest 62.07 of 70", described);
    }

    [Fact]
    public void The_numbers_do_not_depend_on_the_machine_s_locale()
    {
        // A comma decimal separator would make the line unreadable beside a gate written with a
        // point, and these lines are read side by side in one log.
        var probe = new AdaptiveQualityProbe(20, MeetsTarget: false, EncodedBytes: 100,
            Scores(91.53, 88.2, 82.09, 959));

        var described = AdaptiveProbeReport.Describe(probe, Policy);

        Assert.Contains("harmonic 91.53", described);
        Assert.DoesNotContain(",", described.Split("frames")[0].Replace(", ", ""));
    }
}
