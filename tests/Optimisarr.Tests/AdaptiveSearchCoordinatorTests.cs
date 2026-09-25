using Optimisarr.Api.Workers;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Verification;
using Optimisarr.Core.Workers;

namespace Optimisarr.Tests;

/// <summary>
/// Judging what a worker measured, and deciding what it measures next.
///
/// The control plane keeps this decision for a reason: the search brackets and bisects, so a worker
/// that chose its own next candidate would be running a different search from the one the library's
/// quality was defined against. What is pinned here is that a report is judged by exactly the rules
/// a local search uses, and that a worker cannot steer the search by reporting something else.
/// </summary>
public sealed class AdaptiveSearchCoordinatorTests
{
    private const string Passing = """
    {
      "frames": [ { "frameNum": 0, "metrics": { "vmaf": 96.0 } } ],
      "pooled_metrics": { "vmaf": { "min": 94.0, "max": 99.0, "mean": 97.0, "harmonic_mean": 96.5 } }
    }
    """;

    private const string Failing = """
    {
      "frames": [ { "frameNum": 0, "metrics": { "vmaf": 60.0 } } ],
      "pooled_metrics": { "vmaf": { "min": 55.0, "max": 70.0, "mean": 62.0, "harmonic_mean": 61.0 } }
    }
    """;

    private static readonly VerificationPolicy Policy = VerificationPolicy.Default with
    {
        QualityGateEnabled = true,
        MinimumVmafHarmonicMean = 93,
        MinimumVmafMin = 80,
    };

    private static RemoteQualityContract Contract() =>
        new("vmaf_v0.6.1", "Adaptive sample at quality 24", 93, 80, [[], [], []]);

    [Fact]
    public void A_candidate_that_clears_the_gate_is_recorded_as_meeting_the_target()
    {
        var progress = AdaptiveSearchCoordinator.Advance(
            baselineQuality: 24,
            priorProbes: [],
            asked: 24,
            new AdaptiveSearchReport(24, EncodedBytes: 5_000_000, [Passing, Passing, Passing]),
            Contract(),
            Policy);

        Assert.NotNull(progress);
        var probe = Assert.Single(progress!.Probes);
        Assert.Equal(24, probe.Quality);
        Assert.True(probe.MeetsTarget);
        Assert.Equal(5_000_000, probe.EncodedBytes);
        // The library's quality passed, so the search now brackets towards something smaller.
        Assert.NotNull(progress.Decision.NextQuality);
        Assert.True(progress.Decision.NextQuality > 24);
    }

    [Fact]
    public void A_candidate_that_misses_is_recorded_as_missing_and_brackets_the_other_way()
    {
        var progress = AdaptiveSearchCoordinator.Advance(
            24, [], asked: 24,
            new AdaptiveSearchReport(24, 9_000_000, [Failing, Failing, Failing]),
            Contract(), Policy);

        Assert.False(Assert.Single(progress!.Probes).MeetsTarget);
        // Missing the target means looking for a higher quality, which is a smaller number.
        Assert.True(progress.Decision.NextQuality < 24);
    }

    [Fact]
    public void A_worker_cannot_steer_the_search_by_reporting_a_quality_nobody_asked_for()
    {
        // The search is bracketing: accepting an unrequested value would let a worker walk the
        // decision somewhere the control plane never chose, and every later candidate would be
        // derived from it.
        var progress = AdaptiveSearchCoordinator.Advance(
            24, [], asked: 24,
            new AdaptiveSearchReport(Quality: 30, 5_000_000, [Passing, Passing, Passing]),
            Contract(), Policy);

        Assert.Null(progress);
    }

    [Fact]
    public void Unusable_logs_produce_no_probe_rather_than_an_invented_one()
    {
        // A probe is evidence. Recording one the logs do not support would feed the search a
        // measurement that never happened, and the search would then bracket around it.
        Assert.Null(AdaptiveSearchCoordinator.Advance(
            24, [], asked: 24,
            new AdaptiveSearchReport(24, 5_000_000, ["not json at all"]),
            Contract(), Policy));
    }

    [Fact]
    public void Evidence_is_judged_against_the_librarys_policy_exactly_as_a_local_search_is()
    {
        // The same logs, under a stricter policy, are a miss. Note which thresholds decide: the
        // library's live policy, not the contract's copy of them — the same rule a local search
        // applies, so a candidate means the same thing whichever machine encoded it. The contract's
        // thresholds are what the worker was *told* it was measuring against; pinning the judgement
        // to them matters for final verification, where the evidence outlives the request.
        var strict = Policy with { MinimumVmafHarmonicMean = 98 };

        var progress = AdaptiveSearchCoordinator.Advance(
            24, [], asked: 24,
            new AdaptiveSearchReport(24, 5_000_000, [Passing, Passing, Passing]),
            Contract(), strict);

        Assert.False(Assert.Single(progress!.Probes).MeetsTarget);
    }

    [Fact]
    public void A_repeated_report_replaces_its_earlier_measurement_rather_than_piling_up()
    {
        // A retried request, or a worker that never saw the response. The list is persisted between
        // exchanges, so appending would grow it for ever — and the search already takes the last of
        // any duplicates, so the stored evidence should agree with what the decision sees.
        IReadOnlyList<AdaptiveQualityProbe> prior = [new AdaptiveQualityProbe(24, false, 9_000_000)];

        var progress = AdaptiveSearchCoordinator.Advance(
            24, prior, asked: 24,
            new AdaptiveSearchReport(24, 5_000_000, [Passing, Passing, Passing]),
            Contract(), Policy);

        var probe = Assert.Single(progress!.Probes);
        Assert.True(probe.MeetsTarget);
        Assert.Equal(5_000_000, probe.EncodedBytes);
    }

    [Fact]
    public void Prior_probes_carry_forward_so_the_search_can_finish()
    {
        // Four candidates is the bound. Given three already measured, the fourth completes it
        // rather than asking for a fifth.
        IReadOnlyList<AdaptiveQualityProbe> prior =
        [
            new AdaptiveQualityProbe(24, true, 5_000_000),
            new AdaptiveQualityProbe(30, false, 3_000_000),
            new AdaptiveQualityProbe(27, true, 4_000_000),
        ];

        var progress = AdaptiveSearchCoordinator.Advance(
            24, prior, asked: 26,
            new AdaptiveSearchReport(26, 4_500_000, [Passing, Passing, Passing]),
            Contract(), Policy);

        Assert.Equal(4, progress!.Probes.Count);
        Assert.True(progress.Decision.Complete);
        // The smallest measured candidate that actually cleared the target.
        Assert.Equal(27, progress.Decision.SelectedQuality);
    }
}
