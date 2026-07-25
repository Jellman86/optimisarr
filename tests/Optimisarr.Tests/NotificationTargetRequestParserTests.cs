using Optimisarr.Api.Replacement;
using Optimisarr.Core.Domain;

namespace Optimisarr.Tests;

public sealed class NotificationTargetRequestParserTests
{
    [Fact]
    public void Parses_a_valid_request()
    {
        var request = new SaveNotificationTargetRequest(
            " ntfy alerts ", "ntfy", " https://ntfy.sh/topic ", " tok ", Enabled: true,
            NotifyOnReplacement: false, NotifyOnFailure: true);

        Assert.True(NotificationTargetRequestParser.TryParse(request, out var parsed, out var error));
        Assert.Null(error);
        Assert.Equal("ntfy alerts", parsed.Name);
        Assert.Equal(NotificationType.Ntfy, parsed.Type);
        Assert.Equal("https://ntfy.sh/topic", parsed.Url);
        Assert.Equal("tok", parsed.Token);
        Assert.False(parsed.NotifyOnReplacement);
        Assert.True(parsed.NotifyOnFailure);
    }

    [Fact]
    public void Defaults_flags_to_true_when_omitted()
    {
        var request = new SaveNotificationTargetRequest(
            "Hook", "Webhook", "https://h/x", null, Enabled: null,
            NotifyOnReplacement: null, NotifyOnFailure: null);

        Assert.True(NotificationTargetRequestParser.TryParse(request, out var parsed, out _));
        Assert.Null(parsed.Token);
        Assert.True(parsed.Enabled);
        Assert.True(parsed.NotifyOnReplacement);
        Assert.True(parsed.NotifyOnFailure);
    }

    [Theory]
    [InlineData("-1001234567890")]
    [InlineData("123456789")]
    [InlineData("@optimisarr_alerts")]
    public void Parses_a_telegram_chat_with_a_bot_token(string chatId)
    {
        var request = new SaveNotificationTargetRequest(
            "Telegram alerts", "Telegram", chatId, "123456:ABC_def-123", true, true, true);

        Assert.True(NotificationTargetRequestParser.TryParse(request, out var parsed, out var error));
        Assert.Null(error);
        Assert.Equal(NotificationType.Telegram, parsed.Type);
        Assert.Equal(chatId, parsed.Url);
        Assert.Equal("123456:ABC_def-123", parsed.Token);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("channel_without_at")]
    [InlineData("@bad-name")]
    [InlineData("https://t.me/optimisarr_alerts")]
    public void Rejects_an_invalid_telegram_chat_id(string chatId)
    {
        var request = new SaveNotificationTargetRequest(
            "Telegram alerts", "Telegram", chatId, "123456:ABC_def-123", true, true, true);

        Assert.False(NotificationTargetRequestParser.TryParse(request, out _, out var error));
        Assert.Contains("chat ID", error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-bot-token")]
    [InlineData("123456:bad/token")]
    public void Rejects_a_missing_or_invalid_telegram_bot_token(string? token)
    {
        var request = new SaveNotificationTargetRequest(
            "Telegram alerts", "Telegram", "-1001234567890", token, true, true, true);

        Assert.False(NotificationTargetRequestParser.TryParse(request, out _, out var error));
        Assert.Contains("bot token", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Telegram_edit_can_leave_the_bot_token_blank_to_keep_the_stored_secret()
    {
        var request = new SaveNotificationTargetRequest(
            "Telegram alerts", "Telegram", "-1001234567890", null, true, true, true);

        Assert.True(NotificationTargetRequestParser.TryParse(
            request, out var parsed, out var error, hasStoredTelegramToken: true));
        Assert.Null(error);
        Assert.Null(parsed.Token);
    }

    [Fact]
    public void Changing_provider_without_a_new_token_does_not_reuse_the_previous_secret()
    {
        var parsed = new ParsedNotificationTarget(
            "Webhook", NotificationType.Webhook, "https://hook.example/x", null, true, true, true);

        Assert.Null(NotificationTargetRequestParser.ResolveTokenForUpdate(
            NotificationType.Telegram, "123456:ABC_def-123", parsed));
    }

    [Fact]
    public void Rejects_an_unknown_type()
    {
        var request = new SaveNotificationTargetRequest("X", "Email", "https://h/x", null, true, true, true);

        Assert.False(NotificationTargetRequestParser.TryParse(request, out _, out var error));
        Assert.Contains("Type", error);
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://host")]
    [InlineData("")]
    public void Rejects_a_non_http_url(string url)
    {
        var request = new SaveNotificationTargetRequest("X", "Webhook", url, null, true, true, true);

        Assert.False(NotificationTargetRequestParser.TryParse(request, out _, out var error));
        Assert.Contains("URL", error);
    }
}
