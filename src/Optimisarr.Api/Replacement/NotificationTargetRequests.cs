using Optimisarr.Core.Domain;
using Optimisarr.Data;

namespace Optimisarr.Api.Replacement;

/// <summary>A notification target shaped for the client. The token is never returned.</summary>
public sealed record NotificationTargetDto(
    int Id,
    string Name,
    string Type,
    string Url,
    bool HasToken,
    bool Enabled,
    bool NotifyOnReplacement,
    bool NotifyOnFailure,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static NotificationTargetDto From(NotificationTarget target) => new(
        target.Id,
        target.Name,
        target.Type.ToString(),
        target.Url,
        !string.IsNullOrEmpty(target.Token),
        target.Enabled,
        target.NotifyOnReplacement,
        target.NotifyOnFailure,
        target.CreatedAt,
        target.UpdatedAt);
}

public sealed record SaveNotificationTargetRequest(
    string? Name,
    string? Type,
    string? Url,
    string? Token,
    bool? Enabled,
    bool? NotifyOnReplacement,
    bool? NotifyOnFailure);

public sealed record ParsedNotificationTarget(
    string Name,
    NotificationType Type,
    string Url,
    string? Token,
    bool Enabled,
    bool NotifyOnReplacement,
    bool NotifyOnFailure);

public static class NotificationTargetRequestParser
{
    public static bool TryParse(
        SaveNotificationTargetRequest request,
        out ParsedNotificationTarget parsed,
        out string? error)
    {
        parsed = null!;
        error = null;

        var name = request.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "Name is required.";
            return false;
        }

        if (!Enum.TryParse<NotificationType>(request.Type, ignoreCase: true, out var type))
        {
            error = "Type must be one of Webhook, Ntfy, Apprise, or Discord.";
            return false;
        }

        var url = request.Url?.Trim();
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = "URL must be an absolute http(s) URL.";
            return false;
        }

        var token = string.IsNullOrWhiteSpace(request.Token) ? null : request.Token.Trim();
        parsed = new ParsedNotificationTarget(
            name, type, url, token,
            request.Enabled ?? true,
            request.NotifyOnReplacement ?? true,
            request.NotifyOnFailure ?? true);
        return true;
    }
}
