namespace Optimisarr.Core.Diagnostics;

/// <summary>Explicit, bounded consent for enhanced diagnostic events.</summary>
public static class DiagnosticCapturePolicy
{
    public const int MaximumEvents = 10_000;

    public static bool IsAllowedDuration(int? hours) =>
        hours is null or 1 or 24 or 168;

    public static bool AllowsEvent(
        DateTimeOffset startedAt,
        DateTimeOffset? expiresAt,
        DateTimeOffset? stoppedAt,
        int? scopedJobId,
        int? jobId,
        DateTimeOffset nowUtc) =>
        IsRunning(startedAt, expiresAt, stoppedAt, nowUtc)
        && (scopedJobId is null || jobId == scopedJobId);

    public static bool IsRunning(
        DateTimeOffset startedAt,
        DateTimeOffset? expiresAt,
        DateTimeOffset? stoppedAt,
        DateTimeOffset nowUtc) =>
        nowUtc >= startedAt
        && (expiresAt is null || nowUtc < expiresAt)
        && (stoppedAt is null || nowUtc < stoppedAt);
}
