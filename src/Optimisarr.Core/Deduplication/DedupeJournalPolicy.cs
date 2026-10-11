namespace Optimisarr.Core.Deduplication;

public enum DedupeActionState
{
    Prepared,
    Quarantining,
    Quarantined,
    Restoring,
    Restored,
    Cancelled,
    NeedsAttention
}

/// <summary>Journal state describes intent. It never proves filesystem completion.</summary>
public static class DedupeJournalPolicy
{
    public static bool HoldsClaims(DedupeActionState state) =>
        state is not (DedupeActionState.Restored or DedupeActionState.Cancelled);

    public static DedupeActionState AfterRestart(DedupeActionState state) => state switch
    {
        DedupeActionState.Quarantined or DedupeActionState.NeedsAttention or DedupeActionState.Restored
            or DedupeActionState.Cancelled => state,
        _ => DedupeActionState.NeedsAttention
    };

    // Callers must separately prove the relevant file evidence before recording completion.
    public static bool CanTransition(DedupeActionState from, DedupeActionState to) => (from, to) switch
    {
        (DedupeActionState.Prepared, DedupeActionState.Quarantining or DedupeActionState.Cancelled) => true,
        (DedupeActionState.Quarantining, DedupeActionState.Quarantined or DedupeActionState.NeedsAttention) => true,
        (DedupeActionState.Quarantined, DedupeActionState.Restoring or DedupeActionState.NeedsAttention) => true,
        (DedupeActionState.Restoring, DedupeActionState.Restored or DedupeActionState.NeedsAttention) => true,
        (DedupeActionState.NeedsAttention, DedupeActionState.Restoring) => true,
        _ => false
    };
}
