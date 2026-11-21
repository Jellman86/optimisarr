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
    /// <summary>
    /// Whether a library replaces originals without asking. A library that saves its optimised
    /// files in another folder never replaces, even when an older saved row still has both
    /// switches on: saving elsewhere wins, and the two must not both act.
    /// </summary>
    public static bool ReplacesAutomatically(bool autoReplace, bool moveOnComplete) => autoReplace && !moveOnComplete;

    public static bool ShouldReconcile(
        JobStatus status,
        bool? verificationPassed,
        bool libraryAutoReplace,
        bool dryRunMode,
        bool manuallyPaused = false,
        bool superResolutionApplied = false) =>
        status == JobStatus.ReadyToReplace
        && verificationPassed == true
        && libraryAutoReplace
        && !dryRunMode
        && !manuallyPaused
        // No metric can prove an AI upscale looks better, so a person approves every one.
        && !superResolutionApplied;

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
