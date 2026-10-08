namespace Optimisarr.Core.Activity;

public enum PlaybackKind { Episode, Movie, Track, Other }

/// <summary>
/// One live playback on a watched media server: what is playing, who is watching and on which
/// device. Every detail is optional because servers and clients report unevenly; nothing here is
/// guessed. It explains an activity pause and never decides one.
/// </summary>
/// <param name="Title">The item's own title: the episode, film or track name.</param>
/// <param name="Series">The show an episode belongs to.</param>
/// <param name="Artist">The artist of a music track.</param>
/// <param name="User">The media-server account watching.</param>
/// <param name="Device">The player or device name, as the server's own dashboard shows it.</param>
public sealed record PlaybackSession(
    PlaybackKind Kind,
    string? Title,
    string? Series,
    int? Season,
    int? Episode,
    int? Year,
    string? Artist,
    string? User,
    string? Device,
    bool Paused)
{
    /// <summary>The same playback without who is watching or where, for a watcher that hides viewers.</summary>
    public PlaybackSession WithoutViewer() => this with { User = null, Device = null };
}

/// <summary>A playback holding the queue, and the watcher that reported it.</summary>
public sealed record PlaybackHold(string Watcher, PlaybackSession Session);
