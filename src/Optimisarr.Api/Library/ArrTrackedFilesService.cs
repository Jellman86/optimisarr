using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Core.Activity;
using Optimisarr.Core.Domain;
using Optimisarr.Data;

namespace Optimisarr.Api.Library;

/// <summary>What one manager said about the files it tracks.</summary>
/// <param name="Reachable">False when it could not be asked, or what answered was not this manager.</param>
/// <param name="TrackedFiles">Files the manager tracks.</param>
/// <param name="FoundInLibraries">Inventory files matched to those.</param>
/// <param name="Unclear">Inventory files that share a tracked file's name without matching it.</param>
/// <param name="NotFound">Tracked files with no inventory file of that name.</param>
public sealed record ArrConnectionTracking(int Id, string Name, bool Reachable, int TrackedFiles, int FoundInLibraries, int Unclear, int NotFound);

/// <summary>Which inventory files every enabled manager tracks.</summary>
/// <param name="Connections">One entry per enabled manager.</param>
/// <param name="TrackedBy">Inventory file id to the managers whose tracked path it best matches.</param>
/// <param name="PossiblyTrackedBy">Inventory file id to the managers tracking a file of that name.</param>
public sealed record ArrTracking(
    IReadOnlyList<ArrConnectionTracking> Connections,
    IReadOnlyDictionary<int, IReadOnlyList<string>> TrackedBy,
    IReadOnlyDictionary<int, IReadOnlyList<string>> PossiblyTrackedBy)
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
    ILogger<ArrTrackedFilesService> logger,
    TimeSpan? overallTimeout = null)
{
    /// <summary>No redirects: the API key must never follow one to another host.</summary>
    public const string ClientName = "arr-no-redirect";

    private const int ConcurrentSeriesRequests = 4;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);
    private readonly TimeSpan _overallTimeout = overallTimeout ?? TimeSpan.FromMinutes(5);

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
        var trackedBy = new Dictionary<int, IReadOnlyList<string>>();
        var possiblyTrackedBy = new Dictionary<int, IReadOnlyList<string>>();
        foreach (var connection in connections)
        {
            var paths = await TrackedPathsAsync(connection, cancellationToken);
            if (paths is null)
            {
                results.Add(new ArrConnectionTracking(connection.Id, connection.Name, false, 0, 0, 0, 0));
                continue;
            }

            var match = await Task.Run(() => ArrTrackedFileMatcher.Match(paths, inventory, cancellationToken), cancellationToken);
            results.Add(new ArrConnectionTracking(connection.Id, connection.Name, true, paths.Count,
                match.TrackedMediaFileIds.Count, match.PossiblyTrackedMediaFileIds.Count, match.NotFound));
            Attribute(trackedBy, match.TrackedMediaFileIds, connection.Name);
            Attribute(possiblyTrackedBy, match.PossiblyTrackedMediaFileIds, connection.Name);
        }

        return new ArrTracking(results, trackedBy, possiblyTrackedBy);
    }

    private static void Attribute(Dictionary<int, IReadOnlyList<string>> into, IReadOnlySet<int> ids, string name)
    {
        foreach (var id in ids)
        {
            into[id] = into.TryGetValue(id, out var names) ? [.. names, name] : [name];
        }
    }

    private async Task<IReadOnlyList<string>?> TrackedPathsAsync(ArrConnection connection, CancellationToken cancellationToken)
    {
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        overall.CancelAfter(_overallTimeout);
        try
        {
            // An empty list from something else, such as a proxy, must not read as "tracks nothing".
            var status = await GetAsync(connection, "/api/v3/system/status", overall.Token);
            if (status is null || !ArrTrackedFileParser.IsManager(status, connection.Type.ToString()))
            {
                logger.LogWarning("{Name} did not identify itself as {Type}", connection.Name, connection.Type);
                return null;
            }

            if (connection.Type == ArrConnectionType.Radarr)
            {
                var movies = await GetAsync(connection, "/api/v3/movie", overall.Token);
                return movies is null ? null : ArrTrackedFileParser.ParseMovieFilePaths(movies);
            }

            // Sonarr only lists episode files one series at a time.
            var series = await GetAsync(connection, "/api/v3/series", overall.Token);
            if (series is null || ArrTrackedFileParser.ParseSeriesIds(series) is not { } ids) return null;
            var paths = new ConcurrentBag<string>();
            var failed = false;
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
            try
            {
                await Parallel.ForEachAsync(ids, new ParallelOptions { MaxDegreeOfParallelism = ConcurrentSeriesRequests, CancellationToken = stop.Token }, async (id, token) =>
                {
                    var files = await GetAsync(connection, $"/api/v3/episodefile?seriesId={id}", token);
                    if (files is null || ArrTrackedFileParser.ParseEpisodeFilePaths(files) is not { } episodePaths)
                    {
                        failed = true;
                        await stop.CancelAsync();
                        return;
                    }
                    foreach (var path in episodePaths) paths.Add(path);
                });
            }
            catch (OperationCanceledException) when (failed && !overall.IsCancellationRequested)
            {
                return null;
            }
            return failed ? null : [.. paths];
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
        using var response = await httpClientFactory.CreateClient(ClientName).SendAsync(request, timeout.Token);
        if (response.IsSuccessStatusCode) return await response.Content.ReadAsStringAsync(timeout.Token);
        logger.LogWarning("{Name} answered {Status} when asked which files it tracks", connection.Name, (int)response.StatusCode);
        return null;
    }
}
