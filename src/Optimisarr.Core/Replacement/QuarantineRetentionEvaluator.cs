namespace Optimisarr.Core.Replacement;

/// <summary>One quarantined original under consideration for retention purging.</summary>
public sealed record QuarantineEntry(int ReplacementId, DateTimeOffset ReplacedAt);

/// <summary>
/// Pure retention policy: decides which quarantined originals have outlived the
/// configured retention window and may be purged. No clock and no I/O — the caller
/// passes the current time and the candidate entries in, so the decision is
/// deterministic and unit tested. A retention of zero (or negative) means
/// "keep indefinitely", the conservative default, so nothing is ever purged.
/// </summary>
public static class QuarantineRetentionEvaluator
{
    public static IReadOnlyList<int> FindExpired(
        IReadOnlyList<QuarantineEntry> quarantined,
        int retentionDays,
        DateTimeOffset nowUtc)
    {
        if (retentionDays <= 0)
        {
            return [];
        }

        var cutoff = nowUtc - TimeSpan.FromDays(retentionDays);
        return quarantined
            .Where(entry => entry.ReplacedAt <= cutoff)
            .Select(entry => entry.ReplacementId)
            .ToList();
    }
}
