using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Core.Activity;
using Optimisarr.Core.Domain;
using Optimisarr.Data;

namespace Optimisarr.Api.Replacement;

/// <summary>
/// Resolves a backdrop image for a job from the first connected media server (Plex/Jellyfin/Emby)
/// and proxies the bytes, so the Queue hero can show relevant artwork without the browser ever
/// seeing a server token. Best-effort and fully optional: no server, no match, or any error simply
/// yields no image (the hero falls back to its plain look). Resolved URLs are cached briefly so a
/// repeated request does not re-search the server.
/// </summary>
public sealed class ArtworkService(IServiceScopeFactory scopeFactory, IHttpClientFactory httpClientFactory)
{
    private sealed record Resolved(string? Url, string? AuthHeaderName, string? AuthHeaderValue);

    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);
    private readonly ConcurrentDictionary<int, (Resolved Value, DateTime At)> _cache = new();

    public async Task<(byte[] Bytes, string ContentType)?> GetBackdropAsync(int jobId, CancellationToken cancellationToken)
    {
        var resolved = await ResolveAsync(jobId, cancellationToken);
        if (resolved.Url is null)
        {
            return null;
        }

        try
        {
            var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            using var request = new HttpRequestMessage(HttpMethod.Get, resolved.Url);
            request.Headers.TryAddWithoutValidation("Accept", "image/*");
            if (resolved.AuthHeaderName is not null)
            {
                request.Headers.TryAddWithoutValidation(resolved.AuthHeaderName, resolved.AuthHeaderValue);
            }

            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var contentType = response.Content.Headers.ContentType?.ToString() ?? "image/jpeg";
            if (!contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            return bytes.Length > 0 ? (bytes, contentType) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private async Task<Resolved> ResolveAsync(int jobId, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(jobId, out var cached) && DateTime.UtcNow - cached.At < CacheTtl)
        {
            return cached.Value;
        }

        var resolved = await ResolveUncachedAsync(jobId, cancellationToken);
        _cache[jobId] = (resolved, DateTime.UtcNow);
        return resolved;
    }

    private static readonly Resolved None = new(null, null, null);

    private async Task<Resolved> ResolveUncachedAsync(int jobId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();

        var job = await db.Jobs
            .AsNoTracking()
            .Include(j => j.MediaFile)!
            .ThenInclude(m => m!.Library)
            .FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);
        if (job?.MediaFile?.Library is not { } library)
        {
            return None;
        }

        // Only Film/TV have meaningful backdrops.
        if (library.MediaType is not (MediaType.Film or MediaType.Tv))
        {
            return None;
        }

        var isTv = library.MediaType == MediaType.Tv;
        var title = MediaTitleParser.Parse(job.MediaFile.RelativePath, isTv);
        if (title is null)
        {
            return None;
        }

        var watcher = await db.ActivityWatchers
            .AsNoTracking()
            .Where(w => w.Enabled)
            .OrderBy(w => w.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (watcher is null || string.IsNullOrEmpty(watcher.ApiToken))
        {
            return None;
        }

        var root = watcher.BaseUrl.TrimEnd('/');
        try
        {
            var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);

            if (watcher.Type == ActivityWatcherType.Plex)
            {
                var url = $"{root}/search?query={Uri.EscapeDataString(title.Title)}&limit=8";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                request.Headers.TryAddWithoutValidation("X-Plex-Token", watcher.ApiToken);
                using var response = await client.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    return None;
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var art = ArtworkSearchParser.PlexArtPath(json, isTv, title.Year);
                return art is null ? None : new Resolved($"{root}{art}", "X-Plex-Token", watcher.ApiToken);
            }

            // Jellyfin and Emby share the MediaBrowser /Items API.
            var search = $"{root}/Items?searchTerm={Uri.EscapeDataString(title.Title)}"
                + "&IncludeItemTypes=Movie,Series&Recursive=true&Limit=8&Fields=ProductionYear";
            using var jfRequest = new HttpRequestMessage(HttpMethod.Get, search);
            jfRequest.Headers.TryAddWithoutValidation("Accept", "application/json");
            jfRequest.Headers.TryAddWithoutValidation("X-Emby-Token", watcher.ApiToken);
            using var jfResponse = await client.SendAsync(jfRequest, cancellationToken);
            if (!jfResponse.IsSuccessStatusCode)
            {
                return None;
            }

            var jfJson = await jfResponse.Content.ReadAsStringAsync(cancellationToken);
            var path = ArtworkSearchParser.JellyfinBackdropPath(jfJson, isTv, title.Year);
            // Jellyfin image endpoints accept the token as api_key; harmless if not required.
            return path is null
                ? None
                : new Resolved($"{root}{path}&api_key={watcher.ApiToken}", null, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return None;
        }
    }
}
