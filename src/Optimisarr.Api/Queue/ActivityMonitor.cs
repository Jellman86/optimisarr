using Microsoft.EntityFrameworkCore;
using Optimisarr.Core.Activity;
using Optimisarr.Core.Domain;
using Optimisarr.Data;

namespace Optimisarr.Api.Queue;

/// <summary>
/// Polls the configured media-server watchers for active playback and answers
/// whether the queue should pause. Results are cached briefly so the frequent
/// dispatch loop and the queue-status endpoint share one set of HTTP calls rather
/// than hammering Plex/Jellyfin/Emby. A watcher that cannot be reached is reported
/// unreachable, which the pure <see cref="ActivityPauseEvaluator"/> treats as
/// not-active so one offline server can never wedge the queue.
/// </summary>
public sealed class ActivityMonitor(
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpClientFactory,
    TimeProvider timeProvider,
    ILogger<ActivityMonitor> logger)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private ActivityDecision _cached = new(false, null);
    private DateTimeOffset _cachedAt = DateTimeOffset.MinValue;

    public async Task<ActivityDecision> GetActivityAsync(CancellationToken cancellationToken)
    {
        if (timeProvider.GetUtcNow() - _cachedAt < CacheTtl)
        {
            return _cached;
        }

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            // Re-check inside the lock: another caller may have just refreshed.
            if (timeProvider.GetUtcNow() - _cachedAt < CacheTtl)
            {
                return _cached;
            }

            _cached = await MeasureAsync(cancellationToken);
            _cachedAt = timeProvider.GetUtcNow();
            return _cached;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<ActivityDecision> MeasureAsync(CancellationToken cancellationToken)
    {
        List<ActivityWatcher> watchers;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            watchers = await db.ActivityWatchers
                .AsNoTracking()
                .Where(watcher => watcher.Enabled)
                .ToListAsync(cancellationToken);
        }

        if (watchers.Count == 0)
        {
            return new ActivityDecision(false, null);
        }

        var measurements = await Task.WhenAll(
            watchers.Select(watcher => MeasureWatcherAsync(watcher, cancellationToken)));
        return ActivityPauseEvaluator.Evaluate(measurements);
    }

    private async Task<WatcherActivity> MeasureWatcherAsync(ActivityWatcher watcher, CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(RequestTimeout);

            var client = httpClientFactory.CreateClient();
            using var request = BuildRequest(watcher);
            using var response = await client.SendAsync(request, timeoutCts.Token);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(timeoutCts.Token);

            var sessions = watcher.Type == ActivityWatcherType.Plex
                ? PlexSessionsParser.ParseActiveSessions(body)
                : JellyfinSessionsParser.ParseActiveSessions(body);
            return new WatcherActivity(watcher.Name, sessions, Reachable: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Activity watcher {Name} ({Url}) was unreachable", watcher.Name, watcher.BaseUrl);
            return new WatcherActivity(watcher.Name, 0, Reachable: false);
        }
    }

    // Plex returns XML from /status/sessions and takes the token as a header; Jellyfin
    // and Emby share the MediaBrowser /Sessions JSON endpoint and token header.
    private static HttpRequestMessage BuildRequest(ActivityWatcher watcher)
    {
        var baseUrl = watcher.BaseUrl.TrimEnd('/');
        var token = watcher.ApiToken ?? string.Empty;

        if (watcher.Type == ActivityWatcherType.Plex)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/status/sessions");
            request.Headers.TryAddWithoutValidation("X-Plex-Token", token);
            request.Headers.TryAddWithoutValidation("Accept", "application/xml");
            return request;
        }

        var jellyfin = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/Sessions");
        jellyfin.Headers.TryAddWithoutValidation("Authorization", $"MediaBrowser Token=\"{token}\"");
        jellyfin.Headers.TryAddWithoutValidation("X-Emby-Token", token);
        jellyfin.Headers.TryAddWithoutValidation("Accept", "application/json");
        return jellyfin;
    }
}
