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

    [Fact]
    public void Rejects_an_unknown_type()
    {
        var request = new SaveNotificationTargetRequest("X", "Telegram", "https://h/x", null, true, true, true);

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
