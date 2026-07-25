using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Optimisarr.Api.Replacement;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Notifications;
using Optimisarr.Data;

namespace Optimisarr.Tests;

public sealed class NotificationServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<OptimisarrDbContext> _options;

    public NotificationServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:;Foreign Keys=True");
        _connection.Open();
        _options = new DbContextOptionsBuilder<OptimisarrDbContext>().UseSqlite(_connection).Options;
        using var db = new OptimisarrDbContext(_options);
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task Sends_a_replacement_notification_to_a_subscribed_target()
    {
        await SeedAsync(NotificationType.Webhook, "https://hook/x", enabled: true, onReplace: true, onFailure: true);
        var handler = new RecordingHandler();

        await NotifyReplacementAsync(handler);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://hook/x", request.Uri);
        Assert.Contains("replacement", request.Body);
    }

    [Fact]
    public async Task Does_not_send_a_replacement_to_a_failure_only_target()
    {
        await SeedAsync(NotificationType.Ntfy, "https://ntfy.sh/t", enabled: true, onReplace: false, onFailure: true);
        var handler = new RecordingHandler();

        await NotifyReplacementAsync(handler);

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Skips_disabled_targets()
    {
        await SeedAsync(NotificationType.Webhook, "https://hook/x", enabled: false, onReplace: true, onFailure: true);
        var handler = new RecordingHandler();

        await NotifyReplacementAsync(handler);

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Telegram_retries_as_text_when_an_opportunistic_photo_is_rejected()
    {
        await SeedAsync(
            NotificationType.Telegram,
            "-1001234567890",
            enabled: true,
            onReplace: true,
            onFailure: true,
            token: "123456:ABC_def-123");
        var handler = new RecordingHandler(HttpStatusCode.BadRequest, HttpStatusCode.OK)
        {
            FirstResponseBody = "{\"ok\":false,\"error_code\":400,\"description\":\"Bad Request: IMAGE_PROCESS_FAILED\"}"
        };
        var artwork = new StubArtworkProvider(new NotificationImage([0xFF, 0xD8, 0xFF], "image/jpeg"));

        await NotifyReplacementAsync(handler, artwork);

        Assert.Equal(2, handler.Requests.Count);
        Assert.EndsWith("/sendPhoto", handler.Requests[0].Uri);
        Assert.StartsWith("multipart/form-data", handler.Requests[0].ContentType);
        Assert.Contains("name=photo", handler.Requests[0].Body);
        Assert.EndsWith("/sendMessage", handler.Requests[1].Uri);
        Assert.Contains("Optimisarr: replaced a file", handler.Requests[1].Body);
    }

    [Fact]
    public async Task Telegram_does_not_retry_as_text_for_a_generic_bad_request()
    {
        await SeedAsync(
            NotificationType.Telegram,
            "-1001234567890",
            enabled: true,
            onReplace: true,
            onFailure: true,
            token: "123456:ABC_def-123");
        var handler = new RecordingHandler(HttpStatusCode.BadRequest, HttpStatusCode.OK)
        {
            FirstResponseBody = "{\"ok\":false,\"error_code\":400,\"description\":\"Bad Request: chat not found\"}"
        };
        var artwork = new StubArtworkProvider(new NotificationImage([0xFF, 0xD8, 0xFF], "image/jpeg"));

        await NotifyReplacementAsync(handler, artwork);

        var request = Assert.Single(handler.Requests);
        Assert.EndsWith("/sendPhoto", request.Uri);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Telegram_does_not_retry_as_text_when_photo_delivery_is_ambiguous(HttpStatusCode status)
    {
        await SeedAsync(
            NotificationType.Telegram,
            "-1001234567890",
            enabled: true,
            onReplace: true,
            onFailure: true,
            token: "123456:ABC_def-123");
        var handler = new RecordingHandler(status, HttpStatusCode.OK);
        var artwork = new StubArtworkProvider(new NotificationImage([0xFF, 0xD8, 0xFF], "image/jpeg"));

        await NotifyReplacementAsync(handler, artwork);

        var request = Assert.Single(handler.Requests);
        Assert.EndsWith("/sendPhoto", request.Uri);
    }

    [Fact]
    public async Task Telegram_does_not_retry_as_text_after_an_ambiguous_network_failure()
    {
        await SeedAsync(
            NotificationType.Telegram,
            "-1001234567890",
            enabled: true,
            onReplace: true,
            onFailure: true,
            token: "123456:ABC_def-123");
        var handler = new ThrowingHandler();
        var artwork = new StubArtworkProvider(new NotificationImage([0xFF, 0xD8, 0xFF], "image/jpeg"));

        await NotifyReplacementAsync(handler, artwork);

        Assert.Equal(1, handler.RequestCount);
    }

    [Theory]
    [InlineData(NotificationType.Webhook, "https://hook/x", null)]
    [InlineData(NotificationType.Discord, "https://discord.com/api/webhooks/123/token", null)]
    [InlineData(NotificationType.Telegram, "-1001234567890", "123456:ABC_def-123")]
    [InlineData(NotificationType.Ntfy, "https://ntfy.sh/topic", null)]
    [InlineData(NotificationType.Apprise, "https://apprise/notify/key", null)]
    public async Task Test_sends_a_clearly_labelled_message_through_every_provider(
        NotificationType type,
        string url,
        string? token)
    {
        var id = await SeedAsync(type, url, enabled: false, onReplace: false, onFailure: false, token);
        var handler = new RecordingHandler();

        await using var db = new OptimisarrDbContext(_options);
        var service = new NotificationService(
            db,
            new StubHttpClientFactory(handler),
            new StubArtworkProvider(null),
            NullLogger<NotificationService>.Instance);
        var result = await service.TestAsync(id, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.Ok);
        var request = Assert.Single(handler.Requests);
        Assert.Contains("working", request.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Test_reports_provider_rejection_without_throwing()
    {
        var id = await SeedAsync(
            NotificationType.Webhook, "https://hook/x", enabled: true, onReplace: true, onFailure: true);
        var handler = new RecordingHandler(HttpStatusCode.BadRequest);

        await using var db = new OptimisarrDbContext(_options);
        var service = new NotificationService(
            db,
            new StubHttpClientFactory(handler),
            new StubArtworkProvider(null),
            NullLogger<NotificationService>.Instance);
        var result = await service.TestAsync(id, CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Test_failure_logs_do_not_expose_destination_secrets_or_exception_details()
    {
        const string sensitiveSentinel = "destination-redaction-sentinel";
        var id = await SeedAsync(
            NotificationType.Discord,
            $"https://discord.com/api/webhooks/123/{sensitiveSentinel}",
            enabled: true,
            onReplace: true,
            onFailure: true);
        var logger = new RecordingLogger<NotificationService>();

        await using var db = new OptimisarrDbContext(_options);
        var service = new NotificationService(
            db,
            new StubHttpClientFactory(new ThrowingHandler($"request failed for {sensitiveSentinel}")),
            new StubArtworkProvider(null),
            logger);
        var result = await service.TestAsync(id, CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result.Ok);
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains(sensitiveSentinel, StringComparison.Ordinal));
        Assert.All(logger.Entries, entry => Assert.Null(entry.Exception));
    }

    [Fact]
    public async Task A_failing_target_does_not_throw()
    {
        await SeedAsync(NotificationType.Webhook, "https://hook/x", enabled: true, onReplace: true, onFailure: true);

        await NotifyReplacementAsync(new ThrowingHandler());
    }

    private async Task NotifyReplacementAsync(
        HttpMessageHandler handler,
        INotificationArtworkProvider? artwork = null)
    {
        await using var db = new OptimisarrDbContext(_options);
        var service = new NotificationService(
            db,
            new StubHttpClientFactory(handler),
            artwork ?? new StubArtworkProvider(null),
            NullLogger<NotificationService>.Instance);
        await service.NotifyReplacementAsync("/data/Heat.mkv", 2_000_000_000, 1_000_000_000, CancellationToken.None);
    }

    private async Task<int> SeedAsync(
        NotificationType type,
        string url,
        bool enabled,
        bool onReplace,
        bool onFailure,
        string? token = null)
    {
        await using var db = new OptimisarrDbContext(_options);
        var target = new NotificationTarget
        {
            Name = $"{type}",
            Type = type,
            Url = url,
            Token = token,
            Enabled = enabled,
            NotifyOnReplacement = onReplace,
            NotifyOnFailure = onFailure
        };
        db.NotificationTargets.Add(target);
        await db.SaveChangesAsync();
        return target.Id;
    }

    private sealed record CapturedRequest(string Uri, string Body, string ContentType);

    private sealed class RecordingHandler(params HttpStatusCode[] statuses) : HttpMessageHandler
    {
        private int _requestIndex;
        public List<CapturedRequest> Requests { get; } = [];
        public string? FirstResponseBody { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(
                request.RequestUri!.ToString(),
                body,
                request.Content?.Headers.ContentType?.ToString() ?? string.Empty));
            var responseIndex = _requestIndex++;
            var status = responseIndex < statuses.Length ? statuses[responseIndex] : HttpStatusCode.OK;
            var response = new HttpResponseMessage(status);
            if (responseIndex == 0 && FirstResponseBody is not null)
            {
                response.Content = new StringContent(FirstResponseBody);
            }

            return response;
        }
    }

    private sealed class StubArtworkProvider(NotificationImage? image) : INotificationArtworkProvider
    {
        public Task<NotificationImage?> TryGetAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(image);
    }

    private sealed class ThrowingHandler(string message = "connection refused") : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            throw new HttpRequestException(message);
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((formatter(state, exception), exception));
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    public void Dispose() => _connection.Dispose();
}
