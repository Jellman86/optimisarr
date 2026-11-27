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

    /// <summary>
    /// Ask the manager to rescan a title after Optimisarr replaces or restores one of its files, so it
    /// tracks the file now on disk. Existing connections start off, because the rescan was never asked for.
    /// </summary>
    public bool RescanAfterChanges { get; set; } = true;

    /// <summary>
    /// The SHA-256 fingerprint of the password the manager's webhook presents. Only the fingerprint is kept,
    /// so the password is shown once when it is made. Null means the webhook is off.
    /// </summary>
    public string? WebhookSecretFingerprint { get; set; }

    /// <summary>Where the library starts as the manager sees it, when it mounts the files differently.</summary>
    public string? RemotePathPrefix { get; set; }

    /// <summary>The same folder as Optimisarr sees it. Set together with <see cref="RemotePathPrefix"/>.</summary>
    public string? LocalPathPrefix { get; set; }

    /// <summary>When the webhook last received an authenticated delivery, for troubleshooting.</summary>
    public DateTimeOffset? WebhookLastReceivedAt { get; set; }

    public ArrWebhookOutcome? WebhookLastOutcome { get; set; }

    /// <summary>The file or reason behind <see cref="WebhookLastOutcome"/>.</summary>
    public string? WebhookLastDetail { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
