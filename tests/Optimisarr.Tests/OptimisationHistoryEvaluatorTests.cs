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
    public void A_failed_job_for_the_current_version_blocks_re_encoding_but_points_to_retry()
    {
        var history = new OptimisationHistory(LastCompletedAt: null, LastFailedAt: Modified.AddHours(1));

        var result = OptimisationHistoryEvaluator.Apply(Eligible, history, Modified);

        Assert.False(result.IsEligible);
        Assert.Contains("Previously failed", result.Reason);
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
