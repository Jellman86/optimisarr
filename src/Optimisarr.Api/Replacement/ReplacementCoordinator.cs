using System.Collections.Concurrent;

namespace Optimisarr.Api.Replacement;

/// <summary>
/// Serialises replacement per job and per media file so only one destructive operation
/// can act on the same source at a time. A job becomes eligible for replacement the instant it
/// reaches <c>ReadyToReplace</c>, and two independent callers race for it — the worker's
/// post-verify auto-replace and the background auto-replace reconcile sweep (and a manual
/// API replace). Replacement is destructive (it quarantines the original and moves the
/// output into its place), so two overlapping runs corrupt each other: one moves the output
/// into place while the other quarantines what it finds there, and the verified output is
/// lost even though the original is safely restored. This is a process-wide, singleton claim
/// set: a caller that does not win the claim must not proceed.
/// </summary>
public sealed class ReplacementCoordinator
{
    public const int Capacity = 2;
    private readonly ConcurrentDictionary<int, byte> _jobsInFlight = new();
    private readonly ConcurrentDictionary<int, byte> _activeJobs = new();
    private readonly ConcurrentDictionary<int, byte> _mediaInFlight = new();
    private readonly SemaphoreSlim _slots = new(Capacity, Capacity);
    private int _active;
    private int _waiting;

    public int Active => Volatile.Read(ref _active);
    public int Waiting => Volatile.Read(ref _waiting);
    public bool IsActive(int jobId) => _activeJobs.ContainsKey(jobId);

    /// <summary>
    /// Attempts to claim both the job and its source. A losing source claim gives the job claim
    /// back, so a later cycle can retry without ever overlapping another replacement or rollback.
    /// </summary>
    public async Task<bool> TryBeginAsync(int jobId, int mediaFileId, CancellationToken cancellationToken)
    {
        if (!_jobsInFlight.TryAdd(jobId, 0)) return false;
        if (!_mediaInFlight.TryAdd(mediaFileId, 0))
        {
            _jobsInFlight.TryRemove(jobId, out _);
            return false;
        }
        Interlocked.Increment(ref _waiting);
        try
        {
            await _slots.WaitAsync(cancellationToken);
            _activeJobs.TryAdd(jobId, 0);
            Interlocked.Increment(ref _active);
            return true;
        }
        catch
        {
            _mediaInFlight.TryRemove(mediaFileId, out _);
            _jobsInFlight.TryRemove(jobId, out _);
            throw;
        }
        finally
        {
            Interlocked.Decrement(ref _waiting);
        }
    }

    /// <summary>Releases the bounded finalisation slot and both source claims.</summary>
    public void End(int jobId, int mediaFileId)
    {
        _activeJobs.TryRemove(jobId, out _);
        _mediaInFlight.TryRemove(mediaFileId, out _);
        _jobsInFlight.TryRemove(jobId, out _);
        Interlocked.Decrement(ref _active);
        _slots.Release();
    }
}
