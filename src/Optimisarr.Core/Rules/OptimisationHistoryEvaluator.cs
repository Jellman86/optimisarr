namespace Optimisarr.Core.Rules;

/// <summary>
/// What past optimisation jobs say about a file's <em>current</em> version. A job is
/// only relevant if it finished at or after the file was last modified; if the file
/// changed afterwards (a fresh rip, an external re-encode), the old job is stale and
/// the file may be optimised again.
/// </summary>
/// <param name="LastCompletedAt">When the most recent successful job for this file finished, if any.</param>
/// <param name="LastFailedAt">When the most recent failed job for this file finished, if any.</param>
/// <param name="LastFailedJobId">That most recent failed job, so the user can try it again.</param>
public sealed record OptimisationHistory(DateTimeOffset? LastCompletedAt, DateTimeOffset? LastFailedAt, int? LastFailedJobId = null)
{
    /// <summary>When a trim-only job last measured this file and found no silence to trim, and with which settings.</summary>
    public NothingToTrim? LastNothingToTrim { get; init; }

    public static readonly OptimisationHistory None = new(null, null);
}

/// <summary>A trim-only job measured a file's silence with these settings and found nothing to trim.</summary>
public sealed record NothingToTrim(DateTimeOffset At, double ThresholdDb, double MinimumSeconds);

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
        DateTimeOffset fileModifiedAt,
        RuleSettings? rules = null)
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
            return CandidateDecision.HeldBackByFailure("Failed last time, so it isn't tried again by itself", history.LastFailedJobId);
        }

        // A trim-only job with nothing to trim ends without a file, so it is neither a success nor
        // a failure; without this the file would be measured again on every pass. A different
        // threshold or minimum length could find silence the last measurement did not, so either
        // change offers the file again.
        if (baseDecision.TrimsOnly && history.LastNothingToTrim is { } none && IsCurrent(none.At, fileModifiedAt)
            && (rules is null || none.ThresholdDb.Equals(rules.AudioSilenceThresholdDb) && none.MinimumSeconds.Equals(rules.AudioSilenceMinimumSeconds)))
        {
            return CandidateDecision.Skipped("Measured for silence to trim, and there was none");
        }

        return baseDecision;
    }

    private static bool IsCurrent(DateTimeOffset? jobFinishedAt, DateTimeOffset fileModifiedAt) =>
        jobFinishedAt is { } finished && finished >= fileModifiedAt;
}
