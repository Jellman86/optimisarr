using Optimisarr.Core.Domain;

namespace Optimisarr.Data;

/// <summary>
/// A media server Optimisarr polls for active playback. When any enabled watcher
/// is streaming, the queue holds off starting new transcodes so it never competes
/// with someone's playback. The same connection is intended to be reused later for
/// post-replacement library refreshes (see roadmap Phase 8).
/// </summary>
public sealed class ActivityWatcher
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public ActivityWatcherType Type { get; set; } = ActivityWatcherType.Plex;

    /// <summary>Base URL of the server, e.g. <c>http://192.168.1.10:32400</c>.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>The Plex token / Jellyfin / Emby API key used to query sessions.</summary>
    public string? ApiToken { get; set; }

    /// <summary>When false, the watcher is ignored by the pause gate.</summary>
    public bool Enabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
