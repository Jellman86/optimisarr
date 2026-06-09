using Microsoft.EntityFrameworkCore;
using Optimisarr.Core.Activity;
using Optimisarr.Data;

namespace Optimisarr.Api.Library;

/// <summary>
/// Asks each enabled Sonarr/Radarr connection which title folders it is currently
/// importing into, so the enqueue path can hold back files that sit in those folders.
/// Strictly best-effort and self-isolating: a manager that is offline, rejects the
/// key, or returns junk contributes no active imports and is logged, never blocking
/// the queue — exactly like the streaming activity-pause gate.
/// </summary>
public sealed class ArrActivityService(
    OptimisarrDbContext db,
    IHttpClientFactory httpClientFactory,
    ILogger<ArrActivityService> logger)
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    public async Task<IReadOnlyList<ArrActiveImport>> GetActiveImportsAsync(CancellationToken cancellationToken)
    {
        var connections = await db.ArrConnections
            .AsNoTracking()
            .Where(connection => connection.Enabled)
            .ToListAsync(cancellationToken);

        var imports = new List<ArrActiveImport>();
        foreach (var connection in connections)
        {
            imports.AddRange(await GetActiveImportsForAsync(connection, cancellationToken));
        }

        return imports;
    }

    private async Task<IReadOnlyList<ArrActiveImport>> GetActiveImportsForAsync(
        ArrConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            // includeSeries/includeMovie embed the title (with its library path) in each
            // queue record, so one call gives us everything without per-title lookups.
            var url = $"{connection.BaseUrl.TrimEnd('/')}/api/v3/queue" +
                "?pageSize=200&includeSeries=true&includeMovie=true";

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(RequestTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(connection.ApiKey))
            {
                request.Headers.TryAddWithoutValidation("X-Api-Key", connection.ApiKey);
            }

            var client = httpClientFactory.CreateClient();
            using var response = await client.SendAsync(request, timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("{Name} queue query returned {Status}", connection.Name, (int)response.StatusCode);
                return [];
            }

            var json = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            return ArrQueueParser.ParseActiveFolders(json)
                .Select(folder => new ArrActiveImport(connection.Name, folder))
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not query {Name} ({Url}) for active imports", connection.Name, connection.BaseUrl);
            return [];
        }
    }
}
