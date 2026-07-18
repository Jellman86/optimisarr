using Optimisarr.Core.Verification;

namespace Optimisarr.Core.Scheduling;

/// <summary>A deterministic verification outcome that cannot benefit from another automatic encode.</summary>
public enum ImmediateAutoExclusionReason
{
    None,
    SizeSaving,
    VmafAfterHigherQualityRetry,
    VmafAtMaximumQuality
}

/// <summary>
/// Decides when an unrecoverable or repeatedly failing file should be excluded from optimisation,
/// so it stops being offered (and burning encode time) and instead surfaces on the library's
/// Excluded list for the operator to review. Pure and deterministic.
/// </summary>
public static class AutoExclusionPolicy
{
    private const string SizeSavingCheckName = "Size saving";

    /// <summary>
    /// Default number of non-deterministic terminal failures before a file is auto-excluded.
    /// Conservative enough that a transient verdict never buries a good file; deterministic size
    /// and exhausted VMAF outcomes use <see cref="ImmediateReason"/> instead.
    /// </summary>
    public const int DefaultFailureThreshold = 3;

    /// <summary>
    /// Whether a file that has now failed <paramref name="failureCount"/> times should be excluded.
    /// A <paramref name="threshold"/> of zero or less disables auto-exclusion entirely.
    /// </summary>
    public static bool ShouldExclude(int failureCount, int threshold) =>
        threshold > 0 && failureCount >= threshold;

    /// <summary>
    /// Identifies terminal verification outcomes where another encode cannot safely improve the
    /// result. A size-saving failure is excluded rather than silently lowering the configured
    /// quality; an isolated VMAF failure gets the existing higher-quality retry first.
    /// </summary>
    public static ImmediateAutoExclusionReason ImmediateReason(
        VerificationReport report,
        int qualityRetryCount,
        int? effectiveQuality)
    {
        var failed = report.Checks.Where(check => check.Outcome == CheckOutcome.Failed).ToList();
        if (failed.Any(check => string.Equals(
                check.Name,
                SizeSavingCheckName,
                StringComparison.Ordinal)))
        {
            return ImmediateAutoExclusionReason.SizeSaving;
        }

        if (!VmafRetryPolicy.IsSoleVmafFailure(report))
        {
            return ImmediateAutoExclusionReason.None;
        }

        return qualityRetryCount > 0
            ? ImmediateAutoExclusionReason.VmafAfterHigherQualityRetry
            : effectiveQuality is not > 0
                ? ImmediateAutoExclusionReason.VmafAtMaximumQuality
                : ImmediateAutoExclusionReason.None;
    }
}
