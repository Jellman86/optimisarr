namespace Optimisarr.Core.Domain;

/// <summary>How a notification target is reached. All are plain HTTP POSTs that differ only in shape.</summary>
public enum NotificationType
{
    /// <summary>A generic JSON webhook: <c>{ event, title, body }</c> with an optional bearer token.</summary>
    Webhook = 0,

    /// <summary>An ntfy topic URL; the body is plain text and the title rides in a header.</summary>
    Ntfy = 1,

    /// <summary>An Apprise API notify endpoint; posts <c>{ title, body }</c>.</summary>
    Apprise = 2,

    /// <summary>A Discord webhook URL; posts an embed so the title and body render natively.</summary>
    Discord = 3
}
