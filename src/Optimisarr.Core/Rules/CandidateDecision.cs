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
public sealed record CandidateDecision(CandidateOutcome Outcome, string Reason)
{
    public bool IsEligible => Outcome == CandidateOutcome.Eligible;

    public static CandidateDecision Eligible(string reason) => new(CandidateOutcome.Eligible, reason);

    public static CandidateDecision Skipped(string reason) => new(CandidateOutcome.Skipped, reason);
}
