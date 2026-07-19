namespace Optimisarr.Core.Verification;

/// <summary>Allows one deterministic higher-quality retry only when VMAF is the sole failed gate.</summary>
public static class VmafRetryPolicy
{
    private const string VmafCheckName = "Perceptual quality (VMAF)";

    public static bool ShouldRetry(VerificationReport report, int retryCount, int? effectiveQuality)
    {
        if (retryCount > 0 || effectiveQuality is not > 0)
        {
            return false;
        }

        return report.Vmaf?.Measured == true && IsSoleVmafFailure(report);
    }

    /// <summary>Whether VMAF is the report's only failed gate.</summary>
    public static bool IsSoleVmafFailure(VerificationReport report)
    {
        var failed = report.Checks.Where(check => check.Outcome == CheckOutcome.Failed).ToList();
        return failed.Count == 1 && string.Equals(failed[0].Name, VmafCheckName, StringComparison.Ordinal);
    }
}
