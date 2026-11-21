using Optimisarr.Core.Domain;
using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

public sealed class BelowTargetAcceptanceTests
{
    private const string Vmaf = "Perceptual quality (VMAF)";

    private static readonly BelowTargetAcceptanceFacts Eligible = new(
        IsNormalJob: true, IsFailed: true, Kind: MediaKind.Video, SuperResolutionApplied: false,
        SavesToAnotherFolder: false, DryRun: false, HasVerifiedIdentity: true);

    private static VerificationReport Report(
        double harmonic = 94, double minimum = 70, double? fifth = 91,
        bool measured = true, bool withContext = true, params VerificationCheck[] extra) =>
        new(
            [new VerificationCheck("Duration", CheckOutcome.Passed, "ok"),
             new VerificationCheck(Vmaf, CheckOutcome.Failed, "below target"), .. extra],
            Context: withContext
                ? new VerificationContext("libx265", 24, 24, "Crf", 1, "3 windows of 40 s",
                    MinimumVmafHarmonicMean: 95, MinimumVmafFifthPercentile: 90, MinimumVmafCatastrophicMin: 40)
                : null,
            Vmaf: measured
                ? new VmafEvidence(true, null, new QualityScores(95, harmonic, minimum, null, null, VmafFifthPercentile: fifth))
                : new VmafEvidence(false, "no comparable frames", null));

    [Fact]
    public void A_harmonic_mean_just_under_target_can_be_accepted_and_says_by_how_much()
    {
        var decision = BelowTargetAcceptance.Evaluate(Report(harmonic: 94), Eligible);

        Assert.True(decision.Eligible);
        var shortfall = Assert.Single(decision.Shortfalls);
        Assert.Equal(new BelowTargetShortfall(VmafTarget.HarmonicMean, 94, 95), shortfall);
    }

    [Fact]
    public void A_fifth_percentile_under_target_can_be_accepted()
    {
        var decision = BelowTargetAcceptance.Evaluate(Report(harmonic: 96, fifth: 88), Eligible);

        Assert.True(decision.Eligible);
        Assert.Equal(new BelowTargetShortfall(VmafTarget.FifthPercentile, 88, 90), Assert.Single(decision.Shortfalls));
    }

    [Fact]
    public void Without_a_fifth_percentile_the_minimum_is_judged_as_the_evaluator_does()
    {
        var decision = BelowTargetAcceptance.Evaluate(Report(harmonic: 96, minimum: 85, fifth: null), Eligible);

        Assert.True(decision.Eligible);
        Assert.Equal(new BelowTargetShortfall(VmafTarget.FifthPercentile, 85, 90), Assert.Single(decision.Shortfalls));
    }

    [Fact]
    public void A_score_below_the_catastrophic_floor_stays_a_hard_stop()
    {
        var decision = BelowTargetAcceptance.Evaluate(Report(minimum: 12), Eligible);

        Assert.Equal(BelowTargetRefusal.BelowCatastrophicFloor, decision.Refusal);
        Assert.Empty(decision.Shortfalls);
    }

    [Fact]
    public void A_score_that_could_not_be_measured_cannot_be_accepted()
    {
        Assert.Equal(BelowTargetRefusal.QualityNotMeasured,
            BelowTargetAcceptance.Evaluate(Report(measured: false), Eligible).Refusal);
    }

    [Fact]
    public void Any_other_failed_check_means_the_file_is_not_offered()
    {
        var report = Report(extra: new VerificationCheck("Audio tracks", CheckOutcome.Failed, "missing"));

        Assert.Equal(BelowTargetRefusal.OtherChecksFailed, BelowTargetAcceptance.Evaluate(report, Eligible).Refusal);
    }

    [Fact]
    public void A_report_without_its_targets_cannot_be_judged()
    {
        Assert.Equal(BelowTargetRefusal.NoTargets,
            BelowTargetAcceptance.Evaluate(Report(withContext: false), Eligible).Refusal);
    }

    [Fact]
    public void A_vmaf_failure_with_every_score_on_target_has_nothing_to_accept()
    {
        Assert.Equal(BelowTargetRefusal.NoShortfall,
            BelowTargetAcceptance.Evaluate(Report(harmonic: 97, fifth: 93), Eligible).Refusal);
    }

    [Fact]
    public void A_missing_or_malformed_report_is_refused()
    {
        Assert.Equal(BelowTargetRefusal.NoReport, BelowTargetAcceptance.Evaluate(null, Eligible).Refusal);
        Assert.Equal(BelowTargetRefusal.NoReport,
            BelowTargetAcceptance.Evaluate(new VerificationReport([]), Eligible).Refusal);
    }

    [Theory]
    [InlineData(BelowTargetRefusal.NotFailed)]
    [InlineData(BelowTargetRefusal.NotAnOptimisation)]
    [InlineData(BelowTargetRefusal.NotVideo)]
    [InlineData(BelowTargetRefusal.SuperResolution)]
    [InlineData(BelowTargetRefusal.SavesToAnotherFolder)]
    [InlineData(BelowTargetRefusal.DryRun)]
    [InlineData(BelowTargetRefusal.NoVerifiedIdentity)]
    public void Jobs_outside_the_exception_are_refused_with_their_reason(BelowTargetRefusal expected)
    {
        var facts = expected switch
        {
            BelowTargetRefusal.NotFailed => Eligible with { IsFailed = false },
            BelowTargetRefusal.NotAnOptimisation => Eligible with { IsNormalJob = false },
            BelowTargetRefusal.NotVideo => Eligible with { Kind = MediaKind.Audio },
            BelowTargetRefusal.SuperResolution => Eligible with { SuperResolutionApplied = true },
            BelowTargetRefusal.SavesToAnotherFolder => Eligible with { SavesToAnotherFolder = true },
            BelowTargetRefusal.DryRun => Eligible with { DryRun = true },
            _ => Eligible with { HasVerifiedIdentity = false },
        };

        Assert.Equal(expected, BelowTargetAcceptance.Evaluate(Report(), facts).Refusal);
    }

    [Fact]
    public void A_policy_refusal_still_reports_the_shortfall_so_the_reason_can_be_shown_beside_the_score()
    {
        var decision = BelowTargetAcceptance.Evaluate(Report(), Eligible with { DryRun = true });

        Assert.False(decision.Eligible);
        Assert.NotEmpty(decision.Shortfalls);
    }
}
