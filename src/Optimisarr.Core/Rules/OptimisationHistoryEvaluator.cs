namespace Optimisarr.Core.Rules;

/// <summary>
/// What past optimisation jobs say about a file's <em>current</em> version. A job is
/// only relevant if it finished at or after the file was last modified; if the file
/// changed afterwards (a fresh rip, an external re-encode), the old job is stale and
/// the file may be optimised again.
/// </summary>
/// <param name="LastCompletedAt">When the most recent successful job for this file finished, if any.</param>
/// <param name="LastFailedAt">When the most recent failed job for this file finished, if any.</param>
public sealed record OptimisationHistory(DateTimeOffset? LastCompletedAt, DateTimeOffset? LastFailedAt)
{
    public static readonly OptimisationHistory None = new(null, null);
}

/// <summary>
/// Pure overlay that stops a file being optimised over and over. It sits on top of
/// the rule-based <see cref="CandidateEvaluator"/> decision: an otherwise-eligible
/// file that has already been optimised — or that already failed — for its current
/// version is marked skipped with a clear reason, so neither the candidate list nor
/// the enqueue path picks it up again until the file actually changes.
/// </summary>
public static class OptimisationHistoryEvaluator
{
    public static CandidateDecision Apply(
        CandidateDecision baseDecision,
        OptimisationHistory history,
        DateTimeOffset fileModifiedAt)
    {
        // Rule-based skips already carry the most specific reason; leave them alone.
        if (!baseDecision.IsEligible)
        {
            return baseDecision;
        }

        if (IsCurrent(history.LastCompletedAt, fileModifiedAt))
        {
            return CandidateDecision.Skipped("Already optimised");
        }

        if (IsCurrent(history.LastFailedAt, fileModifiedAt))
        {
            return CandidateDecision.Skipped("Previously failed — retry from the Queue page");
        }

        return baseDecision;
    }

    private static bool IsCurrent(DateTimeOffset? jobFinishedAt, DateTimeOffset fileModifiedAt) =>
        jobFinishedAt is { } finished && finished >= fileModifiedAt;
}
