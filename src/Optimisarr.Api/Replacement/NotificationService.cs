using System.Text;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Core.Notifications;
using Optimisarr.Data;

namespace Optimisarr.Api.Replacement;

/// <summary>
/// Sends best-effort notifications to the configured targets when a file is replaced
/// or a job fails. Like the library refresh, this never affects the operation that
/// triggered it: a target that is offline or rejects the POST is logged and skipped.
/// </summary>
public sealed class NotificationService(
    OptimisarrDbContext db,
    IHttpClientFactory httpClientFactory,
    ILogger<NotificationService> logger)
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    public Task NotifyReplacementAsync(string path, long originalBytes, long newBytes, CancellationToken cancellationToken) =>
        DispatchAsync(
            target => target.NotifyOnReplacement,
            "replacement",
            NotificationMessages.ReplacementCompleted(path, originalBytes, newBytes),
            cancellationToken);

    public Task NotifyFailureAsync(string path, string error, CancellationToken cancellationToken) =>
        DispatchAsync(
            target => target.NotifyOnFailure,
            "failure",
            NotificationMessages.JobFailed(path, error),
            cancellationToken);

    private async Task DispatchAsync(
        Func<NotificationTarget, bool> wantsEvent,
        string eventKey,
        NotificationMessage message,
        CancellationToken cancellationToken)
    {
        var targets = await db.NotificationTargets
            .AsNoTracking()
            .Where(target => target.Enabled)
            .ToListAsync(cancellationToken);

        foreach (var target in targets.Where(wantsEvent))
        {
            await SendAsync(target, eventKey, message, cancellationToken);
        }
    }

    private async Task SendAsync(
        NotificationTarget target, string eventKey, NotificationMessage message, CancellationToken cancellationToken)
    {
        try
        {
            var spec = NotificationRequestBuilder.Build(target.Type, target.Url, target.Token, eventKey, message);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(RequestTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Post, spec.Url)
            {
                Content = new StringContent(spec.Body, Encoding.UTF8, spec.ContentType)
            };
            foreach (var (header, value) in spec.Headers)
            {
                request.Headers.TryAddWithoutValidation(header, value);
            }

            var client = httpClientFactory.CreateClient();
            using var response = await client.SendAsync(request, timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Notification to {Name} returned {Status}", target.Name, (int)response.StatusCode);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not notify {Name} ({Url})", target.Name, target.Url);
        }
    }
}
