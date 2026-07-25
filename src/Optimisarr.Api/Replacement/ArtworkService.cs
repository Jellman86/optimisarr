using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Queue;
using Optimisarr.Core.Activity;
using Optimisarr.Core.Domain;
using Optimisarr.Core.IO;
using Optimisarr.Core.Library;
using Optimisarr.Core.Notifications;
using Optimisarr.Data;

namespace Optimisarr.Api.Replacement;

/// <summary>
/// Resolves artwork for a title and proxies the bytes, so the UI can show relevant media images
/// without the browser ever seeing a server token. Posters come first from a connected Radarr/Sonarr
/// — which already holds artwork for the files it manages, so the match is exact — and fall back to
/// the first connected media server (Plex/Jellyfin/Emby). Backdrops come from the media server. All
/// best-effort and fully optional: no source, no match, or any error simply yields no image and the
/// caller falls back to its plain look. Resolved URLs are cached briefly so repeated requests do not
/// re-search.
/// </summary>
public sealed class ArtworkService(
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpClientFactory,
    TranscodeOptions transcodeOptions) : INotificationArtworkProvider
{
    public const string HttpClientName = "ArtworkFetch";

    private sealed record Resolved(string? Url, string? AuthHeaderName, string? AuthHeaderValue);

    private static readonly Resolved None = new(null, null, null);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);
    // The Radarr/Sonarr library list is shared across every poster in a view, so cache it briefly to
    // turn a table render into one list fetch plus local matches rather than one fetch per row.
    private static readonly TimeSpan ArrListTtl = TimeSpan.FromMinutes(10);
    // Remember "no extractable thumbnail" only briefly, so a list scroll doesn't respawn ffmpeg for a
    // cover-less file every time, while a fixed/re-probed file recovers without a long wait.
    private static readonly TimeSpan NegativeTtl = TimeSpan.FromMinutes(30);
    private const int ThumbnailHeight = 240;
    private const int ArtworkMaxBytes = 10 * 1024 * 1024;
    private const int MetadataMaxBytes = 16 * 1024 * 1024;
    private const int FfmpegErrorMaxBytes = 64 * 1024;

    private readonly string _ffmpeg = transcodeOptions.Ffmpeg;
    private readonly ConcurrentDictionary<int, (Resolved Value, DateTime At)> _backdropCache = new();
    private readonly ConcurrentDictionary<int, (Resolved Value, DateTime At)> _posterCache = new();
    private readonly ConcurrentDictionary<int, (string Json, DateTime At)> _arrListCache = new();
    private readonly ConcurrentDictionary<int, DateTime> _noThumbnail = new();

    /// <summary>Backdrop (wide background) for a job's title, from a connected media server.</summary>
    public async Task<(byte[] Bytes, string ContentType)?> GetBackdropAsync(int jobId, CancellationToken cancellationToken)
    {
        var resolved = await CachedAsync(_backdropCache, jobId,
            () => ResolveBackdropAsync(jobId, cancellationToken));
        return await FetchAsync(resolved, cancellationToken);
    }

    /// <summary>
    /// The list thumbnail for a media file, chosen by kind: a poster (Radarr/Sonarr first, then a
    /// media server) for video, the embedded cover art for audio, and a down-scaled still for an
    /// image. Null when nothing is available, so the UI shows its plain placeholder.
    /// </summary>
    public async Task<(byte[] Bytes, string ContentType)?> GetThumbnailAsync(int mediaFileId, CancellationToken cancellationToken)
    {
        if (await LoadMediaAsync(mediaFileId, cancellationToken) is not { } media)
        {
            return null;
        }

        return media.Kind switch
        {
            MediaKind.Audio => await ExtractAsync(mediaFileId, media.Path, MediaThumbnail.CoverArtArguments(media.Path), cancellationToken),
            MediaKind.Image => await ExtractAsync(mediaFileId, media.Path, MediaThumbnail.ImageThumbnailArguments(media.Path, ThumbnailHeight), cancellationToken),
            _ => await GetVideoPosterAsync(mediaFileId, cancellationToken),
        };
    }

    /// <summary>
    /// Finds the affected media file by path and reuses its existing UI thumbnail source for a
    /// notification. Telegram artwork is deliberately opportunistic: anything absent, non-image,
    /// or above the Bot API's 10 MB photo limit becomes a plain-text notification instead.
    /// </summary>
    public async Task<NotificationImage?> TryGetAsync(string path, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var mediaId = await db.MediaFiles
            .AsNoTracking()
            .Where(file => file.Path == path)
            .Select(file => (int?)file.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (mediaId is null)
        {
            return null;
        }

        var artwork = await GetThumbnailAsync(mediaId.Value, cancellationToken);
        return artwork is { Bytes.Length: > 0 and <= ArtworkMaxBytes }
            && artwork.Value.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                ? new NotificationImage(artwork.Value.Bytes, artwork.Value.ContentType)
                : null;
    }

    private async Task<(byte[] Bytes, string ContentType)?> GetVideoPosterAsync(int mediaFileId, CancellationToken cancellationToken)
    {
        var resolved = await CachedAsync(_posterCache, mediaFileId,
            () => ResolvePosterAsync(mediaFileId, cancellationToken));
        return await FetchAsync(resolved, cancellationToken);
    }

    private async Task<(MediaKind Kind, string Path)?> LoadMediaAsync(int mediaFileId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var media = await db.MediaFiles
            .AsNoTracking()
            .Where(file => file.Id == mediaFileId)
            .Select(file => new { file.MediaKind, file.Path })
            .FirstOrDefaultAsync(cancellationToken);
        return media is null ? null : (media.MediaKind, media.Path);
    }

    // Runs ffmpeg to produce the thumbnail bytes on stdout — embedded cover art (audio) or a scaled
    // still (image). A file with no extractable image is remembered briefly so a list re-render does
    // not respawn ffmpeg for it on every scroll. The source path is always a discrete ffmpeg argument
    // (never a shell string), and the process runs with a timeout and captured stdout/stderr.
    private async Task<(byte[] Bytes, string ContentType)?> ExtractAsync(
        int mediaFileId, string path, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        if (_noThumbnail.TryGetValue(mediaFileId, out var at) && DateTime.UtcNow - at < NegativeTtl)
        {
            return null;
        }

        if (!File.Exists(path))
        {
            _noThumbnail[mediaFileId] = DateTime.UtcNow;
            return null;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));

            var info = new ProcessStartInfo
            {
                FileName = _ffmpeg,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }

            using var process = new Process { StartInfo = info };
            process.Start();

            try
            {
                var bytesTask = BoundedStreamReader.ReadAsync(
                    process.StandardOutput.BaseStream, ArtworkMaxBytes, timeout.Token);
                var errorTask = BoundedStreamReader.ReadAsync(
                    process.StandardError.BaseStream, FfmpegErrorMaxBytes, timeout.Token);
                var exitTask = process.WaitForExitAsync(timeout.Token);
                var pending = new List<Task> { bytesTask, errorTask, exitTask };
                var exceededLimit = false;

                while (pending.Count > 0)
                {
                    var completed = await Task.WhenAny(pending);
                    pending.Remove(completed);
                    if ((completed == bytesTask && await bytesTask is null)
                        || (completed == errorTask && await errorTask is null))
                    {
                        exceededLimit = true;
                        process.Kill(entireProcessTree: true);
                        break;
                    }

                    if (completed == exitTask)
                    {
                        break;
                    }
                }

                await process.WaitForExitAsync(timeout.Token);
                var bytes = await bytesTask;
                var errorBytes = await errorTask;
                var contentType = bytes is null ? null : MediaThumbnail.DetectImageContentType(bytes);
                if (exceededLimit
                    || errorBytes is null
                    || process.ExitCode != 0
                    || bytes is null
                    || contentType is null)
                {
                    _noThumbnail[mediaFileId] = DateTime.UtcNow;
                    return null;
                }

                return (bytes, contentType);
            }
            finally
            {
                await EnsureProcessStoppedAsync(process);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // A genuine failure or our own 15s timeout — remember it briefly. A request the caller
            // cancelled is not caught here, so it is not mistaken for a missing thumbnail.
            _noThumbnail[mediaFileId] = DateTime.UtcNow;
            return null;
        }
    }

    private static async Task EnsureProcessStoppedAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            return;
        }
        catch (Win32Exception)
        {
            // Continue to the bounded wait; the process may have exited during the kill attempt.
        }

        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await process.WaitForExitAsync(shutdown.Token);
        }
        catch (InvalidOperationException)
        {
            // The process exited between the state check and wait registration.
        }
        catch (OperationCanceledException)
        {
            // The kill was attempted; do not let cleanup mask the caller's cancellation or timeout.
        }
    }

    private static async Task<Resolved> CachedAsync(
        ConcurrentDictionary<int, (Resolved Value, DateTime At)> cache, int key, Func<Task<Resolved>> resolve)
    {
        if (cache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.At < CacheTtl)
        {
            return cached.Value;
        }

        var resolved = await resolve();
        cache[key] = (resolved, DateTime.UtcNow);
        return resolved;
    }

    private async Task<(byte[] Bytes, string ContentType)?> FetchAsync(Resolved resolved, CancellationToken cancellationToken)
    {
        if (resolved.Url is null)
        {
            return null;
        }

        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, resolved.Url);
            request.Headers.TryAddWithoutValidation("Accept", "image/*");
            if (resolved.AuthHeaderName is not null)
            {
                request.Headers.TryAddWithoutValidation(resolved.AuthHeaderName, resolved.AuthHeaderValue);
            }

            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode
                || response.Content.Headers.ContentLength is > ArtworkMaxBytes)
            {
                return null;
            }

            var contentType = response.Content.Headers.ContentType?.ToString() ?? "image/jpeg";
            if (!contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var bytes = await BoundedStreamReader.ReadAsync(stream, ArtworkMaxBytes, cancellationToken);
            return bytes is { Length: > 0 } ? (bytes, contentType) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static async Task<string?> ReadBoundedTextAsync(
        HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is { } contentLength && contentLength > maxBytes)
        {
            return null;
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        var bytes = await BoundedStreamReader.ReadAsync(stream, maxBytes, cancellationToken);
        return bytes is null ? null : Encoding.UTF8.GetString(bytes);
    }

    private async Task<Resolved> ResolveBackdropAsync(int jobId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();

        var job = await db.Jobs
            .AsNoTracking()
            .Include(j => j.MediaFile)!
            .ThenInclude(m => m!.Library)
            .FirstOrDefaultAsync(j => j.Id == jobId && j.Type == JobType.Normal, cancellationToken);
        if (job?.MediaFile?.Library is not { } library)
        {
            return None;
        }

        var title = TitleFor(library, job.MediaFile.RelativePath);
        if (title is null)
        {
            return None;
        }

        return await ResolveFromWatcherAsync(db, library.MediaType == MediaType.Tv, title, poster: false, cancellationToken);
    }

    private async Task<Resolved> ResolvePosterAsync(int mediaFileId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();

        var media = await db.MediaFiles
            .AsNoTracking()
            .Include(m => m.Library)
            .FirstOrDefaultAsync(m => m.Id == mediaFileId, cancellationToken);
        if (media?.Library is not { } library)
        {
            return None;
        }

        var title = TitleFor(library, media.RelativePath);
        if (title is null)
        {
            return None;
        }

        var isTv = library.MediaType == MediaType.Tv;

        // Radarr/Sonarr first: an exact, local match keyed to the file the manager already imported.
        var arrPoster = await ResolveArrPosterAsync(db, isTv, title, cancellationToken);
        if (arrPoster.Url is not null)
        {
            return arrPoster;
        }

        return await ResolveFromWatcherAsync(db, isTv, title, poster: true, cancellationToken);
    }

    // Only Film/TV have meaningful posters/backdrops; audio and image libraries have none.
    private static MediaTitle? TitleFor(Data.Library library, string relativePath) =>
        library.MediaType is MediaType.Film or MediaType.Tv
            ? MediaTitleParser.Parse(relativePath, library.MediaType == MediaType.Tv)
            : null;

    private async Task<Resolved> ResolveArrPosterAsync(
        OptimisarrDbContext db, bool isTv, MediaTitle title, CancellationToken cancellationToken)
    {
        var arrType = isTv ? ArrConnectionType.Sonarr : ArrConnectionType.Radarr;
        var arr = await db.ArrConnections
            .AsNoTracking()
            .Where(connection => connection.Enabled && connection.Type == arrType)
            .OrderBy(connection => connection.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (arr is null || string.IsNullOrEmpty(arr.ApiKey))
        {
            return None;
        }

        var json = await GetArrListAsync(arr, cancellationToken);
        var path = ArrArtworkParser.PosterPath(json, title.Title, title.Year);
        return path is null
            ? None
            : new Resolved($"{arr.BaseUrl.TrimEnd('/')}{path}", "X-Api-Key", arr.ApiKey);
    }

    private async Task<string?> GetArrListAsync(ArrConnection arr, CancellationToken cancellationToken)
    {
        if (_arrListCache.TryGetValue(arr.Id, out var cached) && DateTime.UtcNow - cached.At < ArrListTtl)
        {
            return cached.Json;
        }

        var path = arr.Type == ArrConnectionType.Radarr ? "/api/v3/movie" : "/api/v3/series";
        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{arr.BaseUrl.TrimEnd('/')}{path}");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("X-Api-Key", arr.ApiKey);
            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await ReadBoundedTextAsync(response.Content, MetadataMaxBytes, cancellationToken);
            if (json is null)
            {
                return null;
            }

            _arrListCache[arr.Id] = (json, DateTime.UtcNow);
            return json;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private async Task<Resolved> ResolveFromWatcherAsync(
        OptimisarrDbContext db, bool isTv, MediaTitle title, bool poster, CancellationToken cancellationToken)
    {
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
            var client = httpClientFactory.CreateClient(HttpClientName);

            if (watcher.Type == ActivityWatcherType.Plex)
            {
                // /hubs/search returns grouped results with art; plain /search often returns only
                // search providers (no Metadata), which is why artwork never resolved there.
                var url = $"{root}/hubs/search?query={Uri.EscapeDataString(title.Title)}&limit=8";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                request.Headers.TryAddWithoutValidation("X-Plex-Token", watcher.ApiToken);
                using var response = await client.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    return None;
                }

                var json = await ReadBoundedTextAsync(response.Content, MetadataMaxBytes, cancellationToken);
                if (json is null)
                {
                    return None;
                }
                var art = poster
                    ? ArtworkSearchParser.PlexPosterPath(json, isTv, title.Year)
                    : ArtworkSearchParser.PlexArtPath(json, isTv, title.Year);
                return art is null ? None : new Resolved($"{root}{art}", "X-Plex-Token", watcher.ApiToken);
            }

            // Jellyfin and Emby share the MediaBrowser /Items API.
            var search = $"{root}/Items?searchTerm={Uri.EscapeDataString(title.Title)}"
                + "&IncludeItemTypes=Movie,Series&Recursive=true&Limit=8&Fields=ProductionYear";
            using var jfRequest = new HttpRequestMessage(HttpMethod.Get, search);
            jfRequest.Headers.TryAddWithoutValidation("Accept", "application/json");
            jfRequest.Headers.TryAddWithoutValidation("X-Emby-Token", watcher.ApiToken);
            using var jfResponse = await client.SendAsync(
                jfRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!jfResponse.IsSuccessStatusCode)
            {
                return None;
            }

            var jfJson = await ReadBoundedTextAsync(jfResponse.Content, MetadataMaxBytes, cancellationToken);
            if (jfJson is null)
            {
                return None;
            }
            var path = poster
                ? ArtworkSearchParser.JellyfinPosterPath(jfJson, isTv, title.Year)
                : ArtworkSearchParser.JellyfinBackdropPath(jfJson, isTv, title.Year);
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
