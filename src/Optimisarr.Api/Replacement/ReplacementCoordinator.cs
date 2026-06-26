using System.Collections.Concurrent;

namespace Optimisarr.Api.Replacement;

/// <summary>
/// Serialises replacement per job so only one <see cref="ReplacementService.ReplaceAsync"/>
/// can act on a given job at a time. A job becomes eligible for replacement the instant it
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
    private readonly ConcurrentDictionary<int, byte> _inFlight = new();

    /// <summary>
    /// Attempts to claim exclusive replacement of <paramref name="jobId"/>. Returns true to the
    /// single winner; every other caller gets false until the winner calls <see cref="End"/>.
    /// </summary>
    public bool TryBegin(int jobId) => _inFlight.TryAdd(jobId, 0);

    /// <summary>Releases the claim taken by <see cref="TryBegin"/> so the job can be replaced again.</summary>
    public void End(int jobId) => _inFlight.TryRemove(jobId, out _);
}
