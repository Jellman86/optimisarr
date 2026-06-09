namespace Optimisarr.Core.Domain;

/// <summary>A Servarr-family manager Optimisarr can ask about in-progress imports.</summary>
public enum ArrConnectionType
{
    /// <summary>Sonarr (TV). Queue records embed a <c>series</c> with its library path.</summary>
    Sonarr = 0,

    /// <summary>Radarr (film). Queue records embed a <c>movie</c> with its library path.</summary>
    Radarr = 1
}
