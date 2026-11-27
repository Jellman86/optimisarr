using Optimisarr.Core.Rules;

namespace Optimisarr.Tests;

public sealed class OptimisationHistoryEvaluatorTests
{
    private static readonly DateTimeOffset Modified = DateTimeOffset.Parse("2026-06-01T00:00:00Z");
    private static readonly CandidateDecision Eligible = CandidateDecision.Eligible("h264 → hevc");

    [Fact]
    public void No_history_leaves_an_eligible_file_eligible()
    {
        var result = OptimisationHistoryEvaluator.Apply(Eligible, OptimisationHistory.None, Modified);

        Assert.True(result.IsEligible);
    }

    [Fact]
    public void A_completed_job_for_the_current_version_marks_it_already_optimised()
    {
        var history = new OptimisationHistory(LastCompletedAt: Modified.AddHours(1), LastFailedAt: null);

        var result = OptimisationHistoryEvaluator.Apply(Eligible, history, Modified);

        Assert.False(result.IsEligible);
        Assert.Equal("Already optimised", result.Reason);
    }

    [Fact]
    public void A_failed_job_for_the_current_version_holds_the_file_back_and_offers_that_job_to_retry()
    {
        var history = new OptimisationHistory(LastCompletedAt: null, LastFailedAt: Modified.AddHours(1), LastFailedJobId: 42);

        var result = OptimisationHistoryEvaluator.Apply(Eligible, history, Modified);

        Assert.False(result.IsEligible);
        Assert.Equal("Failed last time, so it isn't tried again by itself", result.Reason);
        Assert.Equal(42, result.RetryJobId);
    }

    private static readonly RuleSettings TrimRules = RuleProfileDefaults.For(Optimisarr.Core.Domain.RuleProfile.ConservativeHevc)
        with { TrimAudioSilence = true, TrimSilenceWithoutConverting = true, AudioSilenceThresholdDb = -60, AudioSilenceMinimumSeconds = 2 };

    [Fact]
    public void A_trim_only_file_already_measured_with_nothing_to_trim_is_not_offered_again()
    {
        var trimOnly = CandidateDecision.Eligible("Trims silence only") with { TrimsOnly = true };
        var history = new OptimisationHistory(null, null) { LastNothingToTrim = new(Modified.AddHours(1), -60, 2) };

        var result = OptimisationHistoryEvaluator.Apply(trimOnly, history, Modified, TrimRules);

        Assert.False(result.IsEligible);
        Assert.Equal("Measured for silence to trim, and there was none", result.Reason);
    }

    [Fact]
    public void Nothing_to_trim_says_nothing_about_a_file_that_is_converted_or_has_changed_since()
    {
        var history = new OptimisationHistory(null, null) { LastNothingToTrim = new(Modified.AddHours(1), -60, 2) };
        var trimOnly = CandidateDecision.Eligible("Trims silence only") with { TrimsOnly = true };

        Assert.True(OptimisationHistoryEvaluator.Apply(Eligible, history, Modified, TrimRules).IsEligible);
        Assert.True(OptimisationHistoryEvaluator.Apply(trimOnly, history, Modified.AddHours(2), TrimRules).IsEligible);
    }

    [Theory]
    [InlineData(-70, 2)]
    [InlineData(-60, 1)]
    public void A_new_silence_threshold_or_minimum_length_offers_the_file_again(double thresholdDb, double minimumSeconds)
    {
        var history = new OptimisationHistory(null, null) { LastNothingToTrim = new(Modified.AddHours(1), -60, 2) };
        var trimOnly = CandidateDecision.Eligible("Trims silence only") with { TrimsOnly = true };

        var result = OptimisationHistoryEvaluator.Apply(trimOnly, history, Modified,
            TrimRules with { AudioSilenceThresholdDb = thresholdDb, AudioSilenceMinimumSeconds = minimumSeconds });

        Assert.True(result.IsEligible);
    }

    [Fact]
    public void A_stale_failure_offers_nothing_to_retry()
    {
        var history = new OptimisationHistory(LastCompletedAt: null, LastFailedAt: Modified.AddHours(-1), LastFailedJobId: 42);

        var result = OptimisationHistoryEvaluator.Apply(Eligible, history, Modified);

        Assert.True(result.IsEligible);
        Assert.Null(result.RetryJobId);
    }

    [Fact]
    public void An_already_optimised_file_offers_nothing_to_retry_even_after_an_earlier_failure()
    {
        var history = new OptimisationHistory(
            LastCompletedAt: Modified.AddHours(2), LastFailedAt: Modified.AddHours(1), LastFailedJobId: 42);

        var result = OptimisationHistoryEvaluator.Apply(Eligible, history, Modified);

        Assert.Equal("Already optimised", result.Reason);
        Assert.Null(result.RetryJobId);
    }

    [Fact]
    public void A_job_older_than_the_file_is_stale_so_the_file_is_eligible_again()
    {
        // The file was re-modified after the job finished (e.g. a fresh rip).
        var history = new OptimisationHistory(
            LastCompletedAt: Modified.AddHours(-5),
            LastFailedAt: Modified.AddHours(-3));

        var result = OptimisationHistoryEvaluator.Apply(Eligible, history, Modified);

        Assert.True(result.IsEligible);
    }

    [Fact]
    public void A_completed_job_wins_over_a_failed_one()
    {
        var history = new OptimisationHistory(
            LastCompletedAt: Modified.AddHours(2),
            LastFailedAt: Modified.AddHours(1));

        var result = OptimisationHistoryEvaluator.Apply(Eligible, history, Modified);

        Assert.Equal("Already optimised", result.Reason);
    }

    [Fact]
    public void A_rule_based_skip_keeps_its_specific_reason()
    {
        var skipped = CandidateDecision.Skipped("Already hevc (no expected saving)");
        var history = new OptimisationHistory(LastCompletedAt: Modified.AddHours(1), LastFailedAt: null);

        var result = OptimisationHistoryEvaluator.Apply(skipped, history, Modified);

        Assert.Equal("Already hevc (no expected saving)", result.Reason);
    }
}
