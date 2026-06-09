using System.Text;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Core.Activity;
using Optimisarr.Data;

namespace Optimisarr.Api.Replacement;

/// <summary>
/// Tells connected media servers to re-scan a title after a verified replacement (or
/// a rollback), reusing the same connections configured for the activity-pause gate.
/// Strictly best-effort: a server being offline or rejecting the call is logged and
/// ignored, never affecting the replacement's outcome. The original is already safely
/// replaced before this runs.
/// </summary>
public sealed class LibraryRefreshService(
    OptimisarrDbContext db,
    IHttpClientFactory httpClientFactory,
    ILogger<LibraryRefreshService> logger)
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    public async Task RefreshForPathAsync(string changedFilePath, CancellationToken cancellationToken)
    {
        var targets = await db.ActivityWatchers
            .AsNoTracking()
            .Where(watcher => watcher.Enabled && watcher.RefreshOnReplace)
            .ToListAsync(cancellationToken);

        foreach (var watcher in targets)
        {
            await NotifyWatcherAsync(watcher, changedFilePath, cancellationToken);
        }
    }

    private async Task NotifyWatcherAsync(ActivityWatcher watcher, string changedFilePath, CancellationToken cancellationToken)
    {
        try
        {
            var spec = LibraryRefreshRequestBuilder.Build(
                watcher.Type, watcher.BaseUrl, watcher.ApiToken, changedFilePath);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(RequestTimeout);

            using var request = new HttpRequestMessage(new HttpMethod(spec.Method), spec.Url);
            foreach (var (header, value) in spec.Headers)
            {
                request.Headers.TryAddWithoutValidation(header, value);
            }
            if (spec.JsonBody is not null)
            {
                request.Content = new StringContent(spec.JsonBody, Encoding.UTF8, "application/json");
            }

            var client = httpClientFactory.CreateClient();
            using var response = await client.SendAsync(request, timeoutCts.Token);
            if (response.IsSuccessStatusCode)
            {
                logger.LogInformation("Asked {Name} to refresh after replacement of {Path}", watcher.Name, changedFilePath);
            }
            else
            {
                logger.LogWarning("Refresh of {Name} returned {Status}", watcher.Name, (int)response.StatusCode);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not refresh {Name} ({Url}) after replacement", watcher.Name, watcher.BaseUrl);
        }
    }
}
