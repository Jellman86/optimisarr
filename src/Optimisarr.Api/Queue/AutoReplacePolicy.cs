using Optimisarr.Data;

namespace Optimisarr.Api.Queue;

/// <summary>
/// Decides whether a job that is already sitting in ReadyToReplace should be replaced automatically.
/// Auto-replace is evaluated inline the moment a job verifies, but a job can reach ReadyToReplace
/// before its library's "Replace automatically" was enabled (or while a transient replace failure
/// left it there). A periodic reconciliation pass applies this rule so the toggle also covers jobs
/// that are already waiting — not just ones that verify afterwards. Pure so it is unit tested.
/// </summary>
public static class AutoReplacePolicy
{
    public static bool ShouldReconcile(
        JobStatus status,
        bool? verificationPassed,
        bool libraryAutoReplace,
        bool dryRunMode,
        bool manuallyPaused = false) =>
        status == JobStatus.ReadyToReplace
        && verificationPassed == true
        && libraryAutoReplace
        && !dryRunMode
        && !manuallyPaused;

    /// <summary>
    /// Whether a reconciled replacement outcome is a problem worth a warning, or an expected
    /// decline to leave quietly alone.
    ///
    /// Reconciliation runs every few seconds, so anything it logs, it logs constantly. A job the
    /// library's own rules declined — a file that is still hardlinked — is working as configured
    /// and may replace cleanly tomorrow; warning about it on every pass would bury the failures
    /// that do need attention.
    /// </summary>
    public static bool IsFault(Api.Replacement.ReplacementResultKind kind) => kind
        is not Api.Replacement.ReplacementResultKind.Success
        and not Api.Replacement.ReplacementResultKind.AlreadyCompleted
        and not Api.Replacement.ReplacementResultKind.Deferred;
}
