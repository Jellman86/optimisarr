using Optimisarr.Core.Domain;

namespace Optimisarr.Data;

/// <summary>
/// A Sonarr/Radarr connection Optimisarr queries for in-progress imports. While the
/// manager is importing into a title's folder, files in that folder are held back
/// from queueing so a transcode never competes with — or is overwritten by — an
/// import. The API key is write-only: it is stored but never returned to the client.
/// </summary>
public sealed class ArrConnection
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public ArrConnectionType Type { get; set; } = ArrConnectionType.Sonarr;

    /// <summary>Base URL of the manager, e.g. <c>http://192.168.1.10:8989</c>.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>The manager's API key, sent as the <c>X-Api-Key</c> header.</summary>
    public string? ApiKey { get; set; }

    /// <summary>When false, this connection is ignored by the import-exclusion gate.</summary>
    public bool Enabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
