using Optimisarr.Core.Domain;

namespace Optimisarr.Data;

/// <summary>
/// A destination Optimisarr POSTs to when something noteworthy happens — a generic
/// webhook, an ntfy topic, or an Apprise endpoint. Which events fire is controlled
/// per target so a user can, say, be alerted only on failures.
/// </summary>
public sealed class NotificationTarget
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public NotificationType Type { get; set; } = NotificationType.Webhook;

    /// <summary>The POST URL: the webhook, the ntfy topic URL, or the Apprise notify endpoint.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Optional bearer credential sent with the request.</summary>
    public string? Token { get; set; }

    /// <summary>When false, the target is skipped entirely.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Notify when a file is replaced.</summary>
    public bool NotifyOnReplacement { get; set; } = true;

    /// <summary>Notify when a job fails.</summary>
    public bool NotifyOnFailure { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
