using Optimisarr.Core.Replacement;

namespace Optimisarr.Tests;

public sealed class QuarantineRetentionEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Retention_of_zero_keeps_everything_indefinitely()
    {
        var entries = new[]
        {
            new QuarantineEntry(1, Now.AddYears(-1)),
            new QuarantineEntry(2, Now.AddDays(-30))
        };

        var expired = QuarantineRetentionEvaluator.FindExpired(entries, retentionDays: 0, Now);

        Assert.Empty(expired);
    }

    [Fact]
    public void Negative_retention_also_keeps_everything()
    {
        var entries = new[] { new QuarantineEntry(1, Now.AddYears(-5)) };

        var expired = QuarantineRetentionEvaluator.FindExpired(entries, retentionDays: -7, Now);

        Assert.Empty(expired);
    }

    [Fact]
    public void Entries_older_than_the_window_are_expired()
    {
        var entries = new[]
        {
            new QuarantineEntry(1, Now.AddDays(-31)),   // older than 30 days
            new QuarantineEntry(2, Now.AddDays(-29))    // still within the window
        };

        var expired = QuarantineRetentionEvaluator.FindExpired(entries, retentionDays: 30, Now);

        Assert.Equal(new[] { 1 }, expired);
    }

    [Fact]
    public void An_entry_exactly_at_the_cutoff_is_expired()
    {
        var entries = new[] { new QuarantineEntry(7, Now.AddDays(-30)) };

        var expired = QuarantineRetentionEvaluator.FindExpired(entries, retentionDays: 30, Now);

        Assert.Equal(new[] { 7 }, expired);
    }

    [Fact]
    public void Nothing_is_expired_when_all_entries_are_within_the_window()
    {
        var entries = new[]
        {
            new QuarantineEntry(1, Now.AddDays(-1)),
            new QuarantineEntry(2, Now.AddHours(-3))
        };

        var expired = QuarantineRetentionEvaluator.FindExpired(entries, retentionDays: 7, Now);

        Assert.Empty(expired);
    }
}
