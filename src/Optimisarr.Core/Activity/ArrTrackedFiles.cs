using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Optimisarr.Core.Activity;

/// <summary>
/// Reads what a manager reports. An answer that is not what this manager would send is null rather
/// than empty, because "tracks nothing" would let a tracked file be moved.
/// </summary>
public static class ArrTrackedFileParser
{
    /// <summary>The manager named by a <c>/api/v3/system/status</c> response, which proves what answered.</summary>
    public static bool IsManager(string json, string expectedApp)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String
                && document.RootElement.TryGetProperty("appName", out var app) && app.ValueKind == JsonValueKind.String
                && string.Equals(app.GetString(), expectedApp, StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// The file path of every movie in a Radarr <c>/api/v3/movie</c> response. A movie without a file
    /// is ordinary; one that claims a file without saying where makes the whole answer unusable.
    /// </summary>
    public static IReadOnlyList<string>? ParseMovieFilePaths(string json) => ReadArray(json, (movie, paths) =>
    {
        if (!HasId(movie)) return false;
        var claimsFile = movie.TryGetProperty("hasFile", out var hasFile) && hasFile.ValueKind == JsonValueKind.True;
        if (!movie.TryGetProperty("movieFile", out var file) || file.ValueKind != JsonValueKind.Object) return !claimsFile;
        if (ReadPath(file) is not { } path) return false;
        paths.Add(path);
        return true;
    });

    /// <summary>The file paths in a Sonarr <c>/api/v3/episodefile</c> response. Every file must have one.</summary>
    public static IReadOnlyList<string>? ParseEpisodeFilePaths(string json) => ReadArray(json, (file, paths) =>
    {
        if (ReadPath(file) is not { } path) return false;
        paths.Add(path);
        return true;
    });

    /// <summary>The ids in a Sonarr <c>/api/v3/series</c> response. Every series must have one.</summary>
    public static IReadOnlyList<int>? ParseSeriesIds(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return null;
            var ids = new List<int>();
            foreach (var series in document.RootElement.EnumerateArray())
            {
                if (!HasId(series)) return null;
                ids.Add(series.GetProperty("id").GetInt32());
            }
            return ids;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool HasId(JsonElement item) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number;

    private static string? ReadPath(JsonElement file) =>
        file.ValueKind == JsonValueKind.Object && file.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(path.GetString()) ? path.GetString() : null;

    private static List<string>? ReadArray(string json, Func<JsonElement, List<string>, bool> read)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return null;
            var paths = new List<string>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (!read(item, paths)) return null;
            }
            return paths;
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

/// <summary>Which inventory files a manager tracks, and how sure that is.</summary>
/// <param name="TrackedMediaFileIds">The file that best matches each tracked path, when one stands out.</param>
/// <param name="PossiblyTrackedMediaFileIds">
/// Every other file with the name of a tracked file. It may be a spare, or the real one seen through
/// a link or an unusual mount. Only a caller that has a better match to keep can treat it as spare.
/// </param>
/// <param name="Roots">The mounts the files show, used to choose between equally good matches.</param>
/// <param name="NotFound">Tracked paths with no inventory file of that name: normally outside the libraries.</param>
/// <param name="Ambiguous">Tracked paths no one file stood out for.</param>
public sealed record ArrTrackingMatch(
    IReadOnlySet<int> TrackedMediaFileIds,
    IReadOnlySet<int> PossiblyTrackedMediaFileIds,
    IReadOnlyList<ArrPathRoot> Roots,
    int NotFound,
    int Ambiguous);

/// <summary>
/// Works out which inventory files a manager tracks with nothing to configure. The manager and
/// Optimisarr usually see the same disks under different mounts, so the longest shared end of two
/// paths suggests the file, and the mount most files agree on settles a tie. These only rank
/// matches: every file with a tracked file's name stays at least possibly tracked, because a link
/// or a submount can make the real file look like the worse match. Names are compared as a
/// case-insensitive, normalising filesystem would see them, and without the extension, because a
/// file optimised to a new container is still that title until the manager next scans.
/// </summary>
public static class ArrTrackedFileMatcher
{
    public static ArrTrackingMatch Match(
        IReadOnlyList<string> managerPaths, IReadOnlyList<ArrInventoryFile> inventory, CancellationToken cancellationToken = default)
    {
        // Each file is indexed by every ending of its path, so finding the closest match for a
        // tracked path costs its depth, however many files share a name.
        var byEnding = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var byName = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var segmentsOf = new Dictionary<int, string[]>();
        foreach (var file in inventory)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var segments = Segments(file.Path);
            if (segments.Length == 0) continue;
            segmentsOf[file.MediaFileId] = segments;
            foreach (var ending in Endings(segments))
            {
                if (!byEnding.TryGetValue(ending, out var files)) byEnding[ending] = files = [];
                files.Add(file.MediaFileId);
            }
            if (!byName.TryGetValue(segments[^1], out var named)) byName[segments[^1]] = named = [];
            named.Add(file.MediaFileId);
        }

        var tracked = new HashSet<int>();
        var trackedNames = new HashSet<string>(StringComparer.Ordinal);
        var support = new Dictionary<ArrPathRoot, int>();
        var ties = new List<(string[] Manager, List<int> Candidates, int Shared)>();
        var notFound = 0;
        foreach (var path in managerPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manager = Segments(path);
            if (manager.Length == 0) continue;
            if (!byName.ContainsKey(manager[^1]))
            {
                notFound++;
                continue;
            }
            trackedNames.Add(manager[^1]);

            // Longest ending first. A file that only shares its name is not evidence of anything.
            var (candidates, shared) = Closest(manager, byEnding);
            if (shared < 2) continue;
            if (candidates.Count > 1)
            {
                ties.Add((manager, candidates, shared));
                continue;
            }

            tracked.Add(candidates[0]);
            var root = RootOf(manager, segmentsOf[candidates[0]], shared);
            support[root] = support.GetValueOrDefault(root) + 1;
        }

        var agreed = AgreedRoots(support);
        var ambiguous = 0;
        foreach (var (manager, candidates, shared) in ties)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var settled = candidates.Where(id => agreed.Contains(RootOf(manager, segmentsOf[id], shared))).ToList();
            if (settled.Count == 1) tracked.Add(settled[0]);
            else ambiguous++;
        }

        var possibly = trackedNames.SelectMany(name => byName[name]).Where(id => !tracked.Contains(id)).ToHashSet();
        return new ArrTrackingMatch(tracked, possibly, [.. support.Keys], notFound, ambiguous);
    }

    /// <summary>
    /// A mount only settles a tie when clearly more files agree on it than on any other for that
    /// manager folder, so one stray match to a backup cannot redirect the rest.
    /// </summary>
    private static HashSet<ArrPathRoot> AgreedRoots(Dictionary<ArrPathRoot, int> support) =>
        support.GroupBy(entry => entry.Key.ManagerRoot)
            .Select(group => group.OrderByDescending(entry => entry.Value).Take(2).ToList())
            .Where(top => top[0].Value >= 2 && (top.Count == 1 || top[0].Value >= top[1].Value * 2))
            .Select(top => top[0].Key)
            .ToHashSet();

    private static (List<int> Candidates, int Shared) Closest(string[] manager, Dictionary<string, List<int>> byEnding)
    {
        var shared = manager.Length;
        foreach (var ending in Enumerable.Reverse(Endings(manager)))
        {
            if (byEnding.TryGetValue(ending, out var candidates)) return (candidates, shared);
            shared--;
        }
        return ([], 0);
    }

    /// <summary>Every ending of a path, shortest first: the name, then its folder and name, and so on.</summary>
    private static List<string> Endings(string[] segments)
    {
        var endings = new List<string>(segments.Length);
        var ending = new StringBuilder(segments[^1]);
        endings.Add(ending.ToString());
        for (var index = segments.Length - 2; index >= 0; index--)
        {
            ending.Insert(0, '/').Insert(0, segments[index]);
            endings.Add(ending.ToString());
        }
        return endings;
    }

    private static string[] Segments(string path)
    {
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < parts.Length; index++)
        {
            parts[index] = Comparable(index == parts.Length - 1 ? Stem(parts[index]) : parts[index]);
        }
        return parts;
    }

    private static string Comparable(string name) =>
        name.Normalize(NormalizationForm.FormC).TrimEnd(' ', '.').ToUpper(CultureInfo.InvariantCulture);

    private static string Stem(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }

    private static ArrPathRoot RootOf(string[] manager, string[] local, int shared) => new(
        "/" + string.Join('/', manager[..^shared]),
        "/" + string.Join('/', local[..^shared]));
}
