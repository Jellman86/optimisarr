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
    public static string? ResolveTokenForUpdate(
        NotificationType existingType,
        string? existingToken,
        ParsedNotificationTarget parsed) =>
        parsed.Token ?? (existingType == parsed.Type ? existingToken : null);

    public static bool TryParse(
        SaveNotificationTargetRequest request,
        out ParsedNotificationTarget parsed,
        out string? error,
        bool hasStoredTelegramToken = false)
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
            error = "Type must be one of Webhook, Discord, Telegram, Ntfy, or Apprise.";
            return false;
        }

        var url = request.Url?.Trim();
        if (type == NotificationType.Telegram)
        {
            if (!IsTelegramChatId(url))
            {
                error = "Telegram chat ID must be a numeric ID or an @channel username.";
                return false;
            }
        }
        else if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = "URL must be an absolute http(s) URL.";
            return false;
        }

        var token = string.IsNullOrWhiteSpace(request.Token) ? null : request.Token.Trim();
        if (type == NotificationType.Telegram
            && (token is null ? !hasStoredTelegramToken : !IsTelegramBotToken(token)))
        {
            error = "A valid Telegram bot token is required.";
            return false;
        }

        parsed = new ParsedNotificationTarget(
            name, type, url!, token,
            request.Enabled ?? true,
            request.NotifyOnReplacement ?? true,
            request.NotifyOnFailure ?? true);
        return true;
    }

    private static bool IsTelegramBotToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var separator = value.IndexOf(':');
        return separator > 0
            && separator < value.Length - 1
            && value[..separator].All(char.IsAsciiDigit)
            && value[(separator + 1)..]
                .All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
    }

    private static bool IsTelegramChatId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (long.TryParse(value, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var numericId))
        {
            return numericId != 0;
        }

        return value.Length is >= 6 and <= 33
            && value[0] == '@'
            && value[1..].All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
    }
}
