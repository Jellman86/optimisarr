namespace Optimisarr.Core.Scheduling;

/// <summary>
/// Decides when a file that keeps failing should be excluded from optimisation automatically,
/// so it stops being offered (and burning encode time) and instead surfaces on the library's
/// Excluded list for the operator to review. Pure and deterministic.
/// </summary>
public static class AutoExclusionPolicy
{
    /// <summary>
    /// Default number of terminal failures before a file is auto-excluded. Conservative enough that
    /// a single bad verdict (e.g. a transient or false-positive failure) never buries a good file,
    /// and the exclusion is always reversible.
    /// </summary>
    public const int DefaultFailureThreshold = 3;

    /// <summary>
    /// Whether a file that has now failed <paramref name="failureCount"/> times should be excluded.
    /// A <paramref name="threshold"/> of zero or less disables auto-exclusion entirely.
    /// </summary>
    public static bool ShouldExclude(int failureCount, int threshold) =>
        threshold > 0 && failureCount >= threshold;
}
