using Optimisarr.Core.Domain;

namespace Optimisarr.Core.Verification;

/// <summary>Why a failed job cannot be accepted below its picture-quality target.</summary>
public enum BelowTargetRefusal
{
    None = 0,
    NotFailed,
    NotAnOptimisation,
    NoReport,
    OtherChecksFailed,
    QualityNotMeasured,
    NoTargets,
    NoShortfall,
    BelowCatastrophicFloor,
    NotVideo,
    SuperResolution,
    SavesToAnotherFolder,
    DryRun,
    NoVerifiedIdentity
}

public enum VmafTarget
{
    HarmonicMean,
    FifthPercentile
}

/// <summary>One VMAF score that came in under the library's target.</summary>
public sealed record BelowTargetShortfall(VmafTarget Target, double Score, double Minimum);

/// <summary>What is known about the job outside its verification report.</summary>
public sealed record BelowTargetAcceptanceFacts(
    bool IsNormalJob,
    bool IsFailed,
    MediaKind Kind,
    bool SuperResolutionApplied,
    bool SavesToAnotherFolder,
    bool DryRun,
    bool HasVerifiedIdentity);

public sealed record BelowTargetAcceptanceDecision(
    BelowTargetRefusal Refusal,
    IReadOnlyList<BelowTargetShortfall> Shortfalls)
{
    public bool Eligible => Refusal == BelowTargetRefusal.None;
}

/// <summary>
/// Decides whether a person may accept a failed video encode whose only fault is a picture-quality
/// score just under the library's target. This is the one recorded exception to "every gate must
/// pass": the score is a judgement call, so the person looking at the file may make it, one job at a
/// time. Everything that means the file is broken stays a hard stop — any other failed check, a
/// score that could not be measured, and the catastrophic VMAF floor, which is the signature of a
/// corrupt decode rather than a slightly soft picture.
/// </summary>
public static class BelowTargetAcceptance
{
    public static BelowTargetAcceptanceDecision Evaluate(VerificationReport? report, BelowTargetAcceptanceFacts facts)
    {
        if (!facts.IsFailed) return Refused(BelowTargetRefusal.NotFailed);
        if (!facts.IsNormalJob) return Refused(BelowTargetRefusal.NotAnOptimisation);
        if (report is null || !report.HasValidStructure()) return Refused(BelowTargetRefusal.NoReport);
        if (!VmafRetryPolicy.IsSoleVmafFailure(report)) return Refused(BelowTargetRefusal.OtherChecksFailed);
        if (report.Vmaf is not { Measured: true, Scores: { VmafHarmonicMean: { } harmonic, VmafMin: { } minimum } scores })
            return Refused(BelowTargetRefusal.QualityNotMeasured);
        if (report.Context is not { } context) return Refused(BelowTargetRefusal.NoTargets);
        if (minimum < context.MinimumVmafCatastrophicMin) return Refused(BelowTargetRefusal.BelowCatastrophicFloor);

        var shortfalls = new List<BelowTargetShortfall>();
        if (harmonic < context.MinimumVmafHarmonicMean)
            shortfalls.Add(new BelowTargetShortfall(VmafTarget.HarmonicMean, harmonic, context.MinimumVmafHarmonicMean));
        // The evaluator judges the fifth percentile, falling back to the minimum when the measurement
        // has no percentile; read it the same way so the shortfall shown is the one that failed.
        var fifth = scores.VmafFifthPercentile ?? minimum;
        if (fifth < context.MinimumVmafFifthPercentile)
            shortfalls.Add(new BelowTargetShortfall(VmafTarget.FifthPercentile, fifth, context.MinimumVmafFifthPercentile));
        // A VMAF check that failed with every score on target failed for a reason other than the
        // score, so there is nothing here for a person to weigh up.
        if (shortfalls.Count == 0) return Refused(BelowTargetRefusal.NoShortfall);

        var refusal = facts switch
        {
            { Kind: not MediaKind.Video } => BelowTargetRefusal.NotVideo,
            { SuperResolutionApplied: true } => BelowTargetRefusal.SuperResolution,
            // Replace puts the file over the original; a library that saves elsewhere chose not to.
            { SavesToAnotherFolder: true } => BelowTargetRefusal.SavesToAnotherFolder,
            { DryRun: true } => BelowTargetRefusal.DryRun,
            { HasVerifiedIdentity: false } => BelowTargetRefusal.NoVerifiedIdentity,
            _ => BelowTargetRefusal.None
        };
        return new BelowTargetAcceptanceDecision(refusal, shortfalls);
    }

    private static BelowTargetAcceptanceDecision Refused(BelowTargetRefusal refusal) => new(refusal, []);
}
