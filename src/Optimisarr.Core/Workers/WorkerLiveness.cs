namespace Optimisarr.Core.Workers;

/// <summary>
/// When a paired sidecar counts as reachable.
///
/// The interval and the threshold are deliberately different numbers. If a worker were declared
/// offline the moment one beat was late, the status would flap on a single dropped packet or a
/// brief network stall. Allowing several missed beats keeps the signal meaningful while still
/// noticing a machine that has actually gone.
/// </summary>
public static class WorkerLiveness
{
    /// <summary>How often a sidecar should report in.</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    /// <summary>Silence beyond this means offline. Several missed beats, not one.</summary>
    public static readonly TimeSpan OfflineAfter = TimeSpan.FromMinutes(2);

    /// <summary>
    /// True when the worker has reported recently enough to be considered reachable. A worker
    /// that has never reported is offline, not unknown — fail closed, because treating an
    /// unreachable machine as available is what would strand a job on it.
    ///
    /// <paramref name="lastSeenUtc"/> is stamped by the control plane when a heartbeat arrives,
    /// never by the worker, so a sidecar with a wrong clock cannot claim to be alive.
    /// </summary>
    public static bool IsOnline(DateTimeOffset? lastSeenUtc, DateTimeOffset nowUtc) =>
        lastSeenUtc is not null && nowUtc - lastSeenUtc.Value < OfflineAfter;
}
