using System.Text.Json;
using Optimisarr.Core.Domain;

namespace Optimisarr.Core.Notifications;

/// <summary>
/// A provider-agnostic description of the HTTP POST that delivers a notification.
/// Kept free of <c>HttpClient</c> types so it is pure and unit tested; the API layer
/// turns it into a real request.
/// </summary>
public sealed record NotificationImage(byte[] Bytes, string ContentType);

public sealed record NotificationUpload(
    string FieldName,
    string FileName,
    byte[] Bytes,
    string ContentType);

public sealed record NotificationRequest(
    string Url,
    IReadOnlyDictionary<string, string> Headers,
    string Body,
    string ContentType,
    IReadOnlyDictionary<string, string>? FormFields = null,
    NotificationUpload? Upload = null);

/// <summary>
/// Shapes the POST for each notification target type. Webhook and Apprise carry a
/// JSON body; ntfy posts plain text with the title in a header; Discord posts an
/// embed; Telegram posts plain text through the Bot API. An optional token is sent
/// as a bearer credential where the provider supports one. Discord carries its
/// secret in the webhook URL; Telegram uses its bot token in the fixed API path.
/// </summary>
public static class NotificationRequestBuilder
{
    public static NotificationRequest Build(
        NotificationType type,
        string url,
        string? token,
        string eventKey,
        NotificationMessage message,
        NotificationImage? image = null)
    {
        // Discord needs an embed/content payload (a generic webhook body is rejected with 400). We
        // honour the explicit Discord type, and also auto-detect a Discord webhook URL chosen under
        // the generic "Webhook" type, so an existing target like that keeps working. Discord
        // authenticates via the webhook URL itself, so it never takes a token.
        if (type == NotificationType.Discord || IsDiscordWebhookUrl(url))
        {
            return BuildJson(url, new Dictionary<string, string>(),
                new { embeds = new[] { new { title = message.Title, description = message.Body } } });
        }

        if (type == NotificationType.Telegram)
        {
            return BuildTelegram(url, token, message, image);
        }

        var headers = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(token))
        {
            headers["Authorization"] = $"Bearer {token}";
        }

        return type switch
        {
            NotificationType.Ntfy => BuildNtfy(url, headers, message),
            NotificationType.Apprise => BuildJson(url, headers, new { title = message.Title, body = message.Body }),
            _ => BuildJson(url, headers, new { @event = eventKey, title = message.Title, body = message.Body })
        };
    }

    // Discord (and the legacy discordapp.com host) webhook endpoints live under /api/webhooks/.
    private static bool IsDiscordWebhookUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Host.EndsWith("discord.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith("discordapp.com", StringComparison.OrdinalIgnoreCase))
        && uri.AbsolutePath.Contains("/api/webhooks/", StringComparison.OrdinalIgnoreCase);

    private static NotificationRequest BuildNtfy(
        string url, Dictionary<string, string> headers, NotificationMessage message)
    {
        // ntfy reads the notification title from the Title header and the body as text.
        headers["Title"] = message.Title;
        return new NotificationRequest(url, headers, message.Body, "text/plain");
    }

    private static NotificationRequest BuildTelegram(
        string chatId, string? token, NotificationMessage message, NotificationImage? image)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ArgumentException("A Telegram bot token is required.", nameof(token));
        }

        var text = $"{message.Title}\n\n{message.Body}";
        if (image is not null)
        {
            return new NotificationRequest(
                $"https://api.telegram.org/bot{token}/sendPhoto",
                new Dictionary<string, string>(),
                string.Empty,
                "multipart/form-data",
                new Dictionary<string, string>
                {
                    ["chat_id"] = chatId,
                    ["caption"] = LimitTelegramText(text, 1_024)
                },
                new NotificationUpload("photo", ArtworkFileName(image.ContentType), image.Bytes, image.ContentType));
        }

        var endpoint = $"https://api.telegram.org/bot{token}/sendMessage";
        return BuildJson(endpoint, new Dictionary<string, string>(), new
        {
            chat_id = chatId,
            text = LimitTelegramText(text, 4_096)
        });
    }

    private static string LimitTelegramText(string text, int limit)
    {
        if (text.Length <= limit)
        {
            return text;
        }

        var contentLength = limit - 1;
        if (contentLength > 0 && char.IsHighSurrogate(text[contentLength - 1]))
        {
            contentLength--;
        }

        return text[..contentLength] + "…";
    }

    private static string ArtworkFileName(string contentType) => contentType.ToLowerInvariant() switch
    {
        "image/png" => "artwork.png",
        "image/webp" => "artwork.webp",
        _ => "artwork.jpg"
    };

    private static NotificationRequest BuildJson(
        string url, Dictionary<string, string> headers, object payload) =>
        new(url, headers, JsonSerializer.Serialize(payload), "application/json");
}
