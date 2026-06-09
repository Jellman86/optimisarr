namespace Optimisarr.Core.Domain;

/// <summary>A media server Optimisarr can watch for active playback before starting work.</summary>
public enum ActivityWatcherType
{
    Plex = 0,
    Jellyfin = 1,
    Emby = 2
}
