using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Library;
using Optimisarr.Core.Activity;
using Optimisarr.Core.Domain;
using Optimisarr.Data;

namespace Optimisarr.Api.Replacement;

/// <summary>
/// Asks Sonarr or Radarr to rescan the title whose file Optimisarr just replaced or restored, so the manager
/// tracks the file now on disk instead of waiting for its own scheduled scan. It only reads the title list and
/// sends a rescan command: it never deletes, searches, or changes settings. Best effort, like the media server
/// refresh: the file change is already committed, so a failure is logged and nothing more.
/// </summary>
public sealed class ArrRescanService(
    OptimisarrDbContext db,
    IHttpClientFactory httpClientFactory,
    ILogger<ArrRescanService> logger)
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    public async Task RescanForPathAsync(string changedFilePath, CancellationToken cancellationToken)
    {
        var connections = await db.ArrConnections.AsNoTracking()
            .Where(connection => connection.Enabled && connection.RescanAfterChanges)
            .OrderBy(connection => connection.Name)
            .ToListAsync(cancellationToken);

        foreach (var connection in connections)
        {
            await RescanAsync(connection, changedFilePath, cancellationToken);
        }
    }

    private async Task RescanAsync(ArrConnection connection, string changedFilePath, CancellationToken cancellationToken)
    {
        var radarr = connection.Type == ArrConnectionType.Radarr;
        try
        {
            var list = await SendAsync(connection, HttpMethod.Get, radarr ? "/api/v3/movie" : "/api/v3/series", null, cancellationToken);
            if (list is null) return;
            if (ArrTrackedFileParser.ParseTitleFolders(list) is not { } titles)
            {
                logger.LogWarning("{Name} sent a title list Optimisarr could not read", connection.Name);
                return;
            }
            if (ArrRescanTarget.TitleFor(changedFilePath, titles,
                    radarr ? ArrRescanTarget.MovieFolderDepth : ArrRescanTarget.SeriesFolderDepth) is not { } titleId)
            {
                logger.LogInformation("{Name} has no single title whose folder holds {Path}; not asking it to rescan", connection.Name, changedFilePath);
                return;
            }

            var command = radarr
                ? JsonSerializer.Serialize(new { name = "RescanMovie", movieId = titleId })
                : JsonSerializer.Serialize(new { name = "RescanSeries", seriesId = titleId });
            if (await SendAsync(connection, HttpMethod.Post, "/api/v3/command", command, cancellationToken) is not null)
            {
                logger.LogInformation("Asked {Name} to rescan {Kind} {Id} after {Path} changed",
                    connection.Name, radarr ? "movie" : "series", titleId, changedFilePath);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Could not ask {Name} to rescan after {Path} changed: {Reason}", connection.Name, changedFilePath, ex.Message);
        }
    }

    private async Task<string?> SendAsync(ArrConnection connection, HttpMethod method, string path, string? json, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using var request = new HttpRequestMessage(method, connection.BaseUrl.TrimEnd('/') + path);
        if (!string.IsNullOrEmpty(connection.ApiKey))
        {
            request.Headers.TryAddWithoutValidation("X-Api-Key", connection.ApiKey);
        }
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        // No redirects: the API key must never follow one to another host.
        using var response = await httpClientFactory.CreateClient(ArrTrackedFilesService.ClientName).SendAsync(request, timeout.Token);
        if (response.IsSuccessStatusCode) return await response.Content.ReadAsStringAsync(timeout.Token);
        logger.LogWarning("{Name} answered {Status} to {Method} {Path}", connection.Name, (int)response.StatusCode, method, path);
        return null;
    }
}
