using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Optimisarr.Api.Replacement;
using Optimisarr.Core.Domain;
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
    public async Task A_failing_target_does_not_throw()
    {
        await SeedAsync(NotificationType.Webhook, "https://hook/x", enabled: true, onReplace: true, onFailure: true);

        await NotifyReplacementAsync(new ThrowingHandler());
    }

    private async Task NotifyReplacementAsync(HttpMessageHandler handler)
    {
        await using var db = new OptimisarrDbContext(_options);
        var service = new NotificationService(
            db, new StubHttpClientFactory(handler), NullLogger<NotificationService>.Instance);
        await service.NotifyReplacementAsync("/data/Heat.mkv", 2_000_000_000, 1_000_000_000, CancellationToken.None);
    }

    private async Task SeedAsync(NotificationType type, string url, bool enabled, bool onReplace, bool onFailure)
    {
        await using var db = new OptimisarrDbContext(_options);
        db.NotificationTargets.Add(new NotificationTarget
        {
            Name = $"{type}",
            Type = type,
            Url = url,
            Enabled = enabled,
            NotifyOnReplacement = onReplace,
            NotifyOnFailure = onFailure
        });
        await db.SaveChangesAsync();
    }

    private sealed record CapturedRequest(string Uri, string Body);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(request.RequestUri!.ToString(), body));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("connection refused");
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    public void Dispose() => _connection.Dispose();
}
