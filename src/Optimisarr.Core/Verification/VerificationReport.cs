namespace Optimisarr.Core.Verification;

public enum CheckOutcome
{
    Passed = 0,
    Failed = 1
}

/// <summary>One verification gate and why it passed or failed, for display.</summary>
public sealed record VerificationCheck(string Name, CheckOutcome Outcome, string Detail);

/// <summary>The encoder and VMAF inputs behind a report, retained for diagnosis and recovery.</summary>
public sealed record VerificationContext(
    string? VideoEncoder,
    int? RequestedVideoQuality,
    int? EffectiveVideoQuality,
    string? VideoQualityMode,
    int QualityRetryCount,
    string? VmafSampling,
    double MinimumVmafHarmonicMean,
    double MinimumVmafFifthPercentile,
    double MinimumVmafCatastrophicMin);

/// <summary>
/// Structured VMAF evidence retained independently of the pass/fail checks. Measure-only callers
/// such as the blind quality lab can report objective scores without letting them decide whether a
/// personally acceptable sample is playable.
/// </summary>
public sealed record VmafEvidence(
    bool Measured,
    string? Error,
    QualityScores? Scores);

/// <summary>
/// The result of evaluating every verification gate against a converted output.
/// A report passes only when every check passes; a single failure blocks the job
/// from ever moving past <c>ReadyToReplace</c>.
/// </summary>
public sealed record VerificationReport(
    IReadOnlyList<VerificationCheck> Checks,
    VerificationContext? Context = null,
    VmafEvidence? Vmaf = null)
{
    public bool Passed => Checks.All(check => check.Outcome == CheckOutcome.Passed);
}
