using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Core.Domain;
using Optimisarr.Core.IO;
using Optimisarr.Core.Notifications;
using Optimisarr.Data;

namespace Optimisarr.Api.Replacement;

public sealed record NotificationTestResult(bool Ok, string? Error);

/// <summary>
/// Sends best-effort notifications to the configured targets when a file is replaced
/// or a job fails. Like the library refresh, this never affects the operation that
/// triggered it: a target that is offline or rejects the POST is logged and skipped.
/// Telegram targets opportunistically include existing title artwork, with a plain-text
/// retry whenever artwork is unavailable, slow, oversized, or explicitly rejected as invalid media
/// by Telegram.
/// </summary>
public sealed class NotificationService(
    OptimisarrDbContext db,
    IHttpClientFactory httpClientFactory,
    INotificationArtworkProvider artworkProvider,
    ILogger<NotificationService> logger)
{
    private enum DeliveryOutcome
    {
        Delivered,
        PermanentPhotoRejection,
        Failed
    }

    public const string HttpClientName = "NotificationDelivery";
    private const int TelegramErrorMaxBytes = 8 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ArtworkTimeout = TimeSpan.FromSeconds(3);

    public Task NotifyReplacementAsync(string path, long originalBytes, long newBytes, CancellationToken cancellationToken) =>
        DispatchAsync(
            target => target.NotifyOnReplacement,
            "replacement",
            path,
            NotificationMessages.ReplacementCompleted(path, originalBytes, newBytes),
            cancellationToken);

    public Task NotifyFailureAsync(string path, string error, CancellationToken cancellationToken) =>
        DispatchAsync(
            target => target.NotifyOnFailure,
            "failure",
            path,
            NotificationMessages.JobFailed(path, error),
            cancellationToken);

    public async Task<NotificationTestResult?> TestAsync(int targetId, CancellationToken cancellationToken)
    {
        var target = await db.NotificationTargets
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == targetId, cancellationToken);
        if (target is null)
        {
            return null;
        }

        try
        {
            var spec = NotificationRequestBuilder.Build(
                target.Type,
                target.Url,
                target.Token,
                "test",
                NotificationMessages.Test());
            var outcome = await TryPostAsync(target, spec, cancellationToken);
            return outcome == DeliveryOutcome.Delivered
                ? new NotificationTestResult(true, null)
                : new NotificationTestResult(false, "The provider rejected the test notification.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Could not test notification target {Name}", target.Name);
            return new NotificationTestResult(false, "The test notification could not be sent.");
        }
    }

    private async Task DispatchAsync(
        Func<NotificationTarget, bool> wantsEvent,
        string eventKey,
        string path,
        NotificationMessage message,
        CancellationToken cancellationToken)
    {
        var targets = await db.NotificationTargets
            .AsNoTracking()
            .Where(target => target.Enabled)
            .ToListAsync(cancellationToken);
        var subscribed = targets.Where(wantsEvent).ToList();

        NotificationImage? artwork = null;
        if (subscribed.Any(target => target.Type == NotificationType.Telegram))
        {
            try
            {
                using var artworkCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                artworkCts.CancelAfter(ArtworkTimeout);
                artwork = await artworkProvider.TryGetAsync(path, artworkCts.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogDebug(ex, "No notification artwork available for {Path}", path);
            }
        }

        foreach (var target in subscribed)
        {
            await SendAsync(
                target,
                eventKey,
                message,
                target.Type == NotificationType.Telegram ? artwork : null,
                cancellationToken);
        }
    }

    private async Task SendAsync(
        NotificationTarget target,
        string eventKey,
        NotificationMessage message,
        NotificationImage? artwork,
        CancellationToken cancellationToken)
    {
        try
        {
            var spec = NotificationRequestBuilder.Build(
                target.Type, target.Url, target.Token, eventKey, message, artwork);
            var outcome = await TryPostAsync(target, spec, cancellationToken);
            if (outcome == DeliveryOutcome.Delivered)
            {
                return;
            }

            // Artwork is optional. A confirmed permanent photo rejection must not cost the user the
            // notification itself, so retry once through Telegram's text endpoint. Ambiguous network,
            // timeout, rate-limit, and server failures do not retry because the photo may have arrived.
            if (target.Type == NotificationType.Telegram
                && artwork is not null
                && outcome == DeliveryOutcome.PermanentPhotoRejection)
            {
                var fallback = NotificationRequestBuilder.Build(
                    target.Type, target.Url, target.Token, eventKey, message);
                await TryPostAsync(target, fallback, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Could not notify {Name}", target.Name);
        }
    }

    private async Task<DeliveryOutcome> TryPostAsync(
        NotificationTarget target,
        NotificationRequest spec,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(RequestTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Post, spec.Url)
            {
                Content = BuildContent(spec)
            };
            foreach (var (header, value) in spec.Headers)
            {
                request.Headers.TryAddWithoutValidation(header, value);
            }

            var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
            if (response.IsSuccessStatusCode)
            {
                return DeliveryOutcome.Delivered;
            }

            logger.LogWarning("Notification to {Name} returned {Status}", target.Name, (int)response.StatusCode);
            return spec.Upload is not null
                && await IsPermanentPhotoRejectionAsync(response, timeoutCts.Token)
                ? DeliveryOutcome.PermanentPhotoRejection
                : DeliveryOutcome.Failed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Could not notify {Name}", target.Name);
            return DeliveryOutcome.Failed;
        }
    }

    // Only retry when Telegram both rejects the request synchronously with a permanent client status
    // and identifies the problem as photo/media-specific. Generic 400s (bad chat, malformed payload),
    // timeouts, 429s, and 5xx responses stay single-shot because retrying may create a duplicate.
    private static async Task<bool> IsPermanentPhotoRejectionAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if ((int)response.StatusCode is not (400 or 413 or 415 or 422)
            || response.Content.Headers.ContentLength is > TelegramErrorMaxBytes)
        {
            return false;
        }

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var bytes = await BoundedStreamReader.ReadAsync(stream, TelegramErrorMaxBytes, cancellationToken);
            if (bytes is null)
            {
                return false;
            }

            using var document = JsonDocument.Parse(bytes);
            if (!document.RootElement.TryGetProperty("description", out var value)
                || value.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var description = value.GetString();
            return description is not null
                && (description.Contains("photo", StringComparison.OrdinalIgnoreCase)
                    || description.Contains("image", StringComparison.OrdinalIgnoreCase)
                    || description.Contains("media", StringComparison.OrdinalIgnoreCase)
                    || description.Contains("file is too big", StringComparison.OrdinalIgnoreCase)
                    || description.Contains("entity too large", StringComparison.OrdinalIgnoreCase)
                    || description.Contains("wrong type of the web page content", StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static HttpContent BuildContent(NotificationRequest spec)
    {
        if (spec.Upload is null)
        {
            return new StringContent(spec.Body, Encoding.UTF8, spec.ContentType);
        }

        var multipart = new MultipartFormDataContent();
        foreach (var (name, value) in spec.FormFields ?? new Dictionary<string, string>())
        {
            multipart.Add(new StringContent(value, Encoding.UTF8), name);
        }

        var upload = new ByteArrayContent(spec.Upload.Bytes);
        upload.Headers.ContentType = MediaTypeHeaderValue.Parse(spec.Upload.ContentType);
        multipart.Add(upload, spec.Upload.FieldName, spec.Upload.FileName);
        return multipart;
    }
}
