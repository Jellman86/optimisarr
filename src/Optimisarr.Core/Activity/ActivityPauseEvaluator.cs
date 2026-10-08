namespace Optimisarr.Core.Activity;

/// <summary>The measured state of one watched media server at a point in time.</summary>
/// <param name="Name">The watcher's display name, used in the pause reason.</param>
/// <param name="ActiveSessions">Active playback sessions; only meaningful when reachable.</param>
/// <param name="Reachable">False when the server could not be queried (offline, bad token).</param>
/// <param name="Sessions">What each session is playing, where the server reported it.</param>
public sealed record WatcherActivity(string Name, int ActiveSessions, bool Reachable, IReadOnlyList<PlaybackSession>? Sessions = null);

/// <summary>Whether configured services are active right now, and which ones if so.</summary>
/// <param name="Reason">A count-only summary. It is logged, so it never names a viewer or title.</param>
/// <param name="Holds">The playbacks holding the queue, for the people who can already see the app.</param>
public sealed record ActivityDecision(bool Active, string? Reason, IReadOnlyList<PlaybackHold>? Holds = null)
{
    public IReadOnlyList<PlaybackHold> Holds { get; init; } = Holds ?? [];
}

/// <summary>
/// Pure policy for the optional "pause while a media server is streaming" gate. New
/// jobs are held while any reachable watcher reports active playback, so a transcode
/// never competes with someone's stream. An <em>unreachable</em> watcher is treated
/// as not-active on purpose: pausing on the unknown could halt the queue forever
/// because of one offline server or a stale token. Running jobs are never affected.
/// </summary>
public static class ActivityPauseEvaluator
{
    /// <summary>
    /// The playbacks to name beside the queue's pause. Only playback that is the reason counts:
    /// the operator's own pause outranks it (see <c>DispatchPolicyEvaluator</c>).
    /// </summary>
    public static IReadOnlyList<PlaybackHold> HoldsExplainingThePause(ActivityDecision activity, bool manuallyPaused) =>
        activity.Active && !manuallyPaused ? activity.Holds : [];

    public static ActivityDecision Evaluate(IReadOnlyList<WatcherActivity> watchers)
    {
        var streaming = watchers
            .Where(watcher => watcher is { Reachable: true, ActiveSessions: > 0 })
            .ToList();
        if (streaming.Count == 0)
        {
            return new ActivityDecision(false, null);
        }

        var names = string.Join(", ", streaming.Select(watcher => watcher.Name));
        var sessions = streaming.Sum(watcher => watcher.ActiveSessions);
        var plural = sessions == 1 ? "stream" : "streams";
        var holds = streaming
            .SelectMany(watcher => (watcher.Sessions ?? []).Select(session => new PlaybackHold(watcher.Name, session)))
            .ToList();
        return new ActivityDecision(true, $"Paused while {names} {(streaming.Count == 1 ? "is" : "are")} active ({sessions} {plural}).", holds);
    }
}
