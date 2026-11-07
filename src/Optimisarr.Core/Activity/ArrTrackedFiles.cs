using System.Text.Json;

namespace Optimisarr.Core.Activity;

/// <summary>
/// Reads the files a manager tracks. Unlike the queue, an unreadable answer is null rather than
/// empty: "tracks nothing" would let a tracked file be moved.
/// </summary>
public static class ArrTrackedFileParser
{
    /// <summary>The file path of every movie in a Radarr <c>/api/v3/movie</c> response that has one.</summary>
    public static IReadOnlyList<string>? ParseMovieFilePaths(string json) => ReadArray(json, movie =>
        movie.TryGetProperty("movieFile", out var file) && file.ValueKind == JsonValueKind.Object ? Path(file) : null);

    /// <summary>The file paths in a Sonarr <c>/api/v3/episodefile</c> response.</summary>
    public static IReadOnlyList<string>? ParseEpisodeFilePaths(string json) => ReadArray(json, Path);

    /// <summary>The ids in a Sonarr <c>/api/v3/series</c> response.</summary>
    public static IReadOnlyList<int>? ParseSeriesIds(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return null;
            return document.RootElement.EnumerateArray()
                .Where(series => series.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number)
                .Select(series => series.GetProperty("id").GetInt32())
                .ToList();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Path(JsonElement file) =>
        file.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String ? path.GetString() : null;

    private static List<string>? ReadArray(string json, Func<JsonElement, string?> read)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return null;
            return document.RootElement.EnumerateArray().Select(read).OfType<string>().ToList();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>A file in Optimisarr's inventory.</summary>
public sealed record ArrInventoryFile(int MediaFileId, string Path);

/// <summary>The same folder as the manager and as Optimisarr see it.</summary>
public sealed record ArrPathRoot(string ManagerRoot, string LocalRoot);

/// <summary>Which inventory files a manager tracks, and how far that could be told.</summary>
/// <param name="TrackedMediaFileIds">Every file that is, or cannot be ruled out as, tracked.</param>
/// <param name="Roots">The mounts learned from files that matched exactly one way.</param>
/// <param name="Unmatched">Tracked files with no inventory file of that name: outside the libraries.</param>
/// <param name="Ambiguous">Tracked files that several inventory files could be, all of them counted.</param>
public sealed record ArrTrackingMatch(IReadOnlySet<int> TrackedMediaFileIds, IReadOnlyList<ArrPathRoot> Roots, int Unmatched, int Ambiguous);

/// <summary>
/// Works out which inventory files a manager tracks with nothing to configure. The manager and
/// Optimisarr usually see the same disks under different mounts, so the mount is learned from the
/// files themselves: the longest shared end of a path identifies a file, and what comes before it
/// is the mount. Any doubt counts as tracked, because the costly mistake is moving a tracked file;
/// wrongly counting a spare as tracked only leaves a duplicate in place. The extension is ignored
/// because a file optimised to a new container is still that title until the manager next scans.
/// </summary>
public static class ArrTrackedFileMatcher
{
    public static ArrTrackingMatch Match(IReadOnlyList<string> managerPaths, IReadOnlyList<ArrInventoryFile> inventory)
    {
        var byName = inventory
            .Select(file => (file.MediaFileId, Segments: Segments(file.Path)))
            .Where(file => file.Segments.Length > 0)
            .ToLookup(file => Stem(file.Segments[^1]), StringComparer.Ordinal);

        var tracked = new HashSet<int>();
        var roots = new HashSet<ArrPathRoot>();
        var undecided = new List<(string[] Manager, List<(int MediaFileId, string[] Segments)> Candidates)>();
        var unmatched = 0;
        foreach (var path in managerPaths)
        {
            var manager = Segments(path);
            if (manager.Length == 0) continue;
            var named = byName[Stem(manager[^1])].ToList();
            if (named.Count == 0)
            {
                unmatched++;
                continue;
            }

            var closest = named.Max(candidate => SharedEnd(manager, candidate.Segments));
            var candidates = named.Where(candidate => SharedEnd(manager, candidate.Segments) == closest).ToList();
            if (candidates.Count > 1)
            {
                undecided.Add((manager, candidates));
                continue;
            }

            tracked.Add(candidates[0].MediaFileId);
            if (closest >= 2) roots.Add(RootOf(manager, candidates[0].Segments, closest));
        }

        var ambiguous = 0;
        foreach (var (manager, candidates) in undecided)
        {
            var resolved = candidates.Where(candidate => roots.Contains(RootOf(manager, candidate.Segments, SharedEnd(manager, candidate.Segments)))).ToList();
            if (resolved.Count == 1)
            {
                tracked.Add(resolved[0].MediaFileId);
                continue;
            }
            ambiguous++;
            foreach (var candidate in candidates) tracked.Add(candidate.MediaFileId);
        }

        return new ArrTrackingMatch(tracked, [.. roots], unmatched, ambiguous);
    }

    private static string[] Segments(string path) =>
        path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static string Stem(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }

    private static int SharedEnd(string[] left, string[] right)
    {
        if (Stem(left[^1]) != Stem(right[^1])) return 0;
        var shared = 1;
        while (shared < left.Length && shared < right.Length && left[^(shared + 1)] == right[^(shared + 1)]) shared++;
        return shared;
    }

    private static ArrPathRoot RootOf(string[] manager, string[] local, int shared) => new(
        "/" + string.Join('/', manager[..^shared]),
        "/" + string.Join('/', local[..^shared]));
}
