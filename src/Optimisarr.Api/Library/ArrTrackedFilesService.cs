using Microsoft.EntityFrameworkCore;
using Optimisarr.Core.Activity;
using Optimisarr.Core.Domain;
using Optimisarr.Data;

namespace Optimisarr.Api.Library;

/// <summary>What one manager said about the files it tracks.</summary>
/// <param name="Reachable">False when it could not be asked or its answer could not be read.</param>
/// <param name="TrackedFiles">Files the manager tracks.</param>
/// <param name="FoundInLibraries">Inventory files those were matched to.</param>
public sealed record ArrConnectionTracking(string Name, bool Reachable, int TrackedFiles, int FoundInLibraries, int Ambiguous);

/// <summary>Which inventory files every enabled manager tracks.</summary>
/// <param name="Connections">One entry per enabled manager.</param>
/// <param name="TrackedBy">Inventory file id to the managers that track it.</param>
public sealed record ArrTracking(IReadOnlyList<ArrConnectionTracking> Connections, IReadOnlyDictionary<int, IReadOnlyList<string>> TrackedBy)
{
    /// <summary>Every manager answered. Without that, no file can be called untracked.</summary>
    public bool Complete => Connections.All(connection => connection.Reachable);
}

/// <summary>
/// Asks the enabled Sonarr and Radarr connections which files they track. Only reads. A manager that
/// cannot be asked makes the answer incomplete rather than empty, so callers fail closed.
/// </summary>
public sealed class ArrTrackedFilesService(
    OptimisarrDbContext db,
    IHttpClientFactory httpClientFactory,
    ILogger<ArrTrackedFilesService> logger)
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

    public async Task<ArrTracking> GetAsync(CancellationToken cancellationToken)
    {
        var connections = await db.ArrConnections.AsNoTracking()
            .Where(connection => connection.Enabled)
            .OrderBy(connection => connection.Name)
            .ToListAsync(cancellationToken);
        var inventory = await db.MediaFiles.AsNoTracking()
            .Select(file => new ArrInventoryFile(file.Id, file.Path))
            .ToListAsync(cancellationToken);

        var results = new List<ArrConnectionTracking>();
        var trackedBy = new Dictionary<int, List<string>>();
        foreach (var connection in connections)
        {
            var paths = await TrackedPathsAsync(connection, cancellationToken);
            if (paths is null)
            {
                results.Add(new ArrConnectionTracking(connection.Name, false, 0, 0, 0));
                continue;
            }

            var match = ArrTrackedFileMatcher.Match(paths, inventory);
            results.Add(new ArrConnectionTracking(connection.Name, true, paths.Count, match.TrackedMediaFileIds.Count, match.Ambiguous));
            foreach (var id in match.TrackedMediaFileIds)
            {
                if (!trackedBy.TryGetValue(id, out var names)) trackedBy[id] = names = [];
                names.Add(connection.Name);
            }
        }

        return new ArrTracking(results, trackedBy.ToDictionary(entry => entry.Key, entry => (IReadOnlyList<string>)entry.Value));
    }

    private async Task<IReadOnlyList<string>?> TrackedPathsAsync(ArrConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            if (connection.Type == ArrConnectionType.Radarr)
            {
                var movies = await GetAsync(connection, "/api/v3/movie", cancellationToken);
                return movies is null ? null : ArrTrackedFileParser.ParseMovieFilePaths(movies);
            }

            // Sonarr only lists episode files one series at a time.
            var series = await GetAsync(connection, "/api/v3/series", cancellationToken);
            if (series is null || ArrTrackedFileParser.ParseSeriesIds(series) is not { } ids) return null;
            var paths = new List<string>();
            foreach (var id in ids)
            {
                var files = await GetAsync(connection, $"/api/v3/episodefile?seriesId={id}", cancellationToken);
                if (files is null || ArrTrackedFileParser.ParseEpisodeFilePaths(files) is not { } episodePaths) return null;
                paths.AddRange(episodePaths);
            }
            return paths;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Could not ask {Name} which files it tracks: {Reason}", connection.Name, ex.Message);
            return null;
        }
    }

    private async Task<string?> GetAsync(ArrConnection connection, string path, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, connection.BaseUrl.TrimEnd('/') + path);
        if (!string.IsNullOrEmpty(connection.ApiKey))
        {
            request.Headers.TryAddWithoutValidation("X-Api-Key", connection.ApiKey);
        }
        using var response = await httpClientFactory.CreateClient().SendAsync(request, timeout.Token);
        if (response.IsSuccessStatusCode) return await response.Content.ReadAsStringAsync(timeout.Token);
        logger.LogWarning("{Name} answered {Status} when asked which files it tracks", connection.Name, (int)response.StatusCode);
        return null;
    }
}
