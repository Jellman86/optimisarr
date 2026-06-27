using Optimisarr.Core.Rules;

namespace Optimisarr.Tests;

public sealed class OptimisedSiblingEvaluatorTests
{
    private static readonly CandidateDecision Eligible = CandidateDecision.Eligible("h264 → hevc");

    [Fact]
    public void Skips_an_eligible_file_when_an_optimised_copy_already_sits_beside_it()
    {
        var result = OptimisedSiblingEvaluator.Apply(Eligible, hasOptimisedSibling: true);

        Assert.False(result.IsEligible);
        Assert.Contains("optimised copy already exists", result.Reason);
    }

    [Fact]
    public void Leaves_an_eligible_file_eligible_when_no_optimised_sibling_exists()
    {
        var result = OptimisedSiblingEvaluator.Apply(Eligible, hasOptimisedSibling: false);

        Assert.True(result.IsEligible);
        Assert.Equal("h264 → hevc", result.Reason);
    }

    [Fact]
    public void Leaves_an_already_skipped_decision_untouched_so_its_specific_reason_survives()
    {
        var skipped = CandidateDecision.Skipped("HDR is excluded");

        var result = OptimisedSiblingEvaluator.Apply(skipped, hasOptimisedSibling: true);

        Assert.False(result.IsEligible);
        Assert.Equal("HDR is excluded", result.Reason);
    }
}
