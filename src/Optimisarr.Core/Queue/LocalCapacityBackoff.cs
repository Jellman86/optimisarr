using System.Collections.Concurrent;

namespace Optimisarr.Core.Queue;

/// <summary>Bound local reclaims after resource pressure without holding work away from workers.</summary>
public sealed class LocalCapacityBackoff(TimeProvider clock)
{
    private sealed record HoldState(int Attempt, long Started);
    private readonly ConcurrentDictionary<int, HoldState> _holds = new();
    private bool Expired(HoldState hold) => clock.GetElapsedTime(hold.Started) >= TimeSpan.FromSeconds(30);

    public void Hold(int jobId, int attempt)
    {
        var hold = new HoldState(attempt, clock.GetTimestamp());
        _holds.AddOrUpdate(jobId, hold, (_, previous) => previous.Attempt > attempt ? previous : hold);
    }
    public bool IsHeld(int jobId, int attempt) => _holds.TryGetValue(jobId, out var hold)
        && hold.Attempt == attempt && !Expired(hold);
    public void PruneExpired()
    {
        foreach (var hold in _holds)
            if (Expired(hold.Value)) _holds.TryRemove(hold);
    }
}
