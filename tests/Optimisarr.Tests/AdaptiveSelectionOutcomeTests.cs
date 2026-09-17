using Microsoft.Extensions.Logging;
using Optimisarr.Api.Workers;

namespace Optimisarr.Tests;

/// <summary>
/// A search that learned nothing and one that chose a value are different events, and were logged
/// as though they were the same one.
/// </summary>
public sealed class AdaptiveSelectionOutcomeTests
{
    [Fact]
    public void A_search_that_learned_nothing_is_a_warning()
    {
        // The case that cost a hundred and seven jobs: the search ran, every candidate came back
        // unusable, the library's quality was used, the encode came out larger than the source and
        // failed the size gate. The line saying so was Information, among the ordinary ones.
        Assert.Equal(
            LogLevel.Warning,
            AdaptiveSelectionOutcome.SeverityOf(fellBack: true, expected: false));
    }

    [Fact]
    public void A_search_that_chose_a_value_is_ordinary()
    {
        Assert.Equal(
            LogLevel.Information,
            AdaptiveSelectionOutcome.SeverityOf(fellBack: false, expected: false));
    }

    [Fact]
    public void A_library_with_nothing_to_search_against_is_not_a_warning()
    {
        // With no quality gate there is nothing to measure candidates against, so using the
        // library's value is the configured answer rather than a failure. Warning here would put
        // one in front of an operator for every job in such a library, and teach them to ignore
        // the ones that matter.
        Assert.Equal(
            LogLevel.Information,
            AdaptiveSelectionOutcome.SeverityOf(fellBack: true, expected: true));
    }

    [Fact]
    public void The_three_outcomes_do_not_share_a_sentence()
    {
        var chose = AdaptiveSelectionOutcome.Describe(fellBack: false, expected: false);
        var configured = AdaptiveSelectionOutcome.Describe(fellBack: true, expected: true);
        var learnedNothing = AdaptiveSelectionOutcome.Describe(fellBack: true, expected: false);

        Assert.Equal(3, new HashSet<string> { chose, configured, learnedNothing }.Count);
        Assert.Contains("learned nothing", learnedNothing, StringComparison.Ordinal);
        Assert.DoesNotContain("learned nothing", configured, StringComparison.Ordinal);
    }
}
