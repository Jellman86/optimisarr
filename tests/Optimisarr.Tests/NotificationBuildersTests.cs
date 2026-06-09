using Optimisarr.Core.Domain;
using Optimisarr.Core.Notifications;

namespace Optimisarr.Tests;

public sealed class NotificationMessagesTests
{
    [Fact]
    public void Replacement_message_reports_the_saving()
    {
        var message = NotificationMessages.ReplacementCompleted("/data/Heat.mkv", 2_000_000_000, 1_000_000_000);

        Assert.Contains("replaced", message.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/data/Heat.mkv", message.Body);
        Assert.Contains("50%", message.Body);
    }

    [Fact]
    public void Replacement_message_handles_a_zero_size_original()
    {
        var message = NotificationMessages.ReplacementCompleted("/data/x.mkv", 0, 0);

        Assert.Contains("0%", message.Body);
    }

    [Fact]
    public void Failure_message_includes_the_path_and_error()
    {
        var message = NotificationMessages.JobFailed("/data/x.mkv", "ffmpeg exited with code 1");

        Assert.Contains("failed", message.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/data/x.mkv", message.Body);
        Assert.Contains("ffmpeg exited", message.Body);
    }
}

public sealed class NotificationRequestBuilderTests
{
    private static readonly NotificationMessage Message = new("Title here", "Body here");

    [Fact]
    public void Webhook_posts_json_with_the_event_and_optional_bearer_token()
    {
        var request = NotificationRequestBuilder.Build(
            NotificationType.Webhook, "https://hooks.example.com/x", "secret", "replacement", Message);

        Assert.Equal("https://hooks.example.com/x", request.Url);
        Assert.Equal("application/json", request.ContentType);
        Assert.Equal("Bearer secret", request.Headers["Authorization"]);
        Assert.Contains("\"event\":\"replacement\"", request.Body);
        Assert.Contains("Title here", request.Body);
    }

    [Fact]
    public void Webhook_without_a_token_sends_no_authorization_header()
    {
        var request = NotificationRequestBuilder.Build(
            NotificationType.Webhook, "https://h/x", null, "failure", Message);

        Assert.False(request.Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public void Ntfy_posts_plain_text_with_the_title_header()
    {
        var request = NotificationRequestBuilder.Build(
            NotificationType.Ntfy, "https://ntfy.sh/mytopic", null, "replacement", Message);

        Assert.Equal("text/plain", request.ContentType);
        Assert.Equal("Title here", request.Headers["Title"]);
        Assert.Equal("Body here", request.Body);
    }

    [Fact]
    public void Apprise_posts_json_title_and_body()
    {
        var request = NotificationRequestBuilder.Build(
            NotificationType.Apprise, "https://apprise/notify/key", null, "replacement", Message);

        Assert.Equal("application/json", request.ContentType);
        Assert.Contains("\"title\":\"Title here\"", request.Body);
        Assert.Contains("\"body\":\"Body here\"", request.Body);
        Assert.DoesNotContain("event", request.Body);
    }

    [Fact]
    public void Discord_posts_an_embed_so_the_message_renders()
    {
        var request = NotificationRequestBuilder.Build(
            NotificationType.Discord, "https://discord.com/api/webhooks/1/abc", null, "replacement", Message);

        Assert.Equal("application/json", request.ContentType);
        Assert.Contains("\"embeds\"", request.Body);
        Assert.Contains("\"title\":\"Title here\"", request.Body);
        Assert.Contains("\"description\":\"Body here\"", request.Body);
    }

    [Fact]
    public void Discord_does_not_send_a_bearer_token_because_the_webhook_url_carries_the_secret()
    {
        var request = NotificationRequestBuilder.Build(
            NotificationType.Discord, "https://discord.com/api/webhooks/1/abc", "secret", "replacement", Message);

        Assert.False(request.Headers.ContainsKey("Authorization"));
    }
}
