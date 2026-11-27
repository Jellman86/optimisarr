namespace Optimisarr.Core.Rules;

public enum CandidateOutcome
{
    Eligible = 0,
    Skipped = 1
}

/// <summary>
/// The result of evaluating one file against a rule profile, with a human-readable
/// reason so the UI can always explain why a file is or isn't a candidate.
/// </summary>
/// <param name="RetryJobId">The failed job that holds this file back, when trying that job again is the way forward.</param>
public sealed record CandidateDecision(CandidateOutcome Outcome, string Reason, int? RetryJobId = null)
{
    public bool IsEligible => Outcome == CandidateOutcome.Eligible;

    /// <summary>A music file that is not converted, only trimmed of silence. Whether there is any is measured by the job.</summary>
    public bool TrimsOnly { get; init; }

    public static CandidateDecision Eligible(string reason) => new(CandidateOutcome.Eligible, reason);

    public static CandidateDecision Skipped(string reason) => new(CandidateOutcome.Skipped, reason);

    public static CandidateDecision HeldBackByFailure(string reason, int? failedJobId) =>
        new(CandidateOutcome.Skipped, reason, failedJobId);
}
