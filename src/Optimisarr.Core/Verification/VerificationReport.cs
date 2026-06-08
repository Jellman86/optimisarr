namespace Optimisarr.Core.Verification;

public enum CheckOutcome
{
    Passed = 0,
    Failed = 1
}

/// <summary>One verification gate and why it passed or failed, for display.</summary>
public sealed record VerificationCheck(string Name, CheckOutcome Outcome, string Detail);

/// <summary>
/// The result of evaluating every verification gate against a converted output.
/// A report passes only when every check passes; a single failure blocks the job
/// from ever moving past <c>ReadyToReplace</c>.
/// </summary>
public sealed record VerificationReport(IReadOnlyList<VerificationCheck> Checks)
{
    public bool Passed => Checks.All(check => check.Outcome == CheckOutcome.Passed);
}
