using Optimisarr.Core.Notifications;

namespace Optimisarr.Api.Replacement;

/// <summary>
/// Supplies optional artwork for richer notifications. Implementations must treat missing,
/// slow, oversized, or unsupported artwork as a normal no-image result.
/// </summary>
public interface INotificationArtworkProvider
{
    Task<NotificationImage?> TryGetAsync(string path, CancellationToken cancellationToken);
}
