using System.Text.Json;

namespace Optimisarr.Core.Activity;

/// <summary>
/// Pure parser for a Sonarr/Radarr <c>/api/v3/queue</c> response (requested with
/// <c>includeSeries</c>/<c>includeMovie</c> so each record embeds its title). It
/// extracts the library folder of every queued item — i.e. every title the manager
/// is currently grabbing or importing — so Optimisarr can avoid optimising a file
/// while an import is about to land in the same folder.
/// </summary>
public static class ArrQueueParser
{
    /// <summary>
    /// Returns the distinct embedded <c>series.path</c> / <c>movie.path</c> values from
    /// the queue. Any record in the queue is treated as active; a record without an
    /// embedded title path is ignored. Malformed JSON yields an empty list rather than
    /// throwing, so one bad response can never wedge the queue.
    /// </summary>
    public static IReadOnlyList<string> ParseActiveFolders(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("records", out var records)
                || records.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var folders = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var record in records.EnumerateArray())
            {
                var folder = ReadTitlePath(record, "series") ?? ReadTitlePath(record, "movie");
                if (folder is not null && seen.Add(folder))
                {
                    folders.Add(folder);
                }
            }

            return folders;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? ReadTitlePath(JsonElement record, string titleProperty)
    {
        if (record.ValueKind == JsonValueKind.Object
            && record.TryGetProperty(titleProperty, out var title)
            && title.ValueKind == JsonValueKind.Object
            && title.TryGetProperty("path", out var path)
            && path.ValueKind == JsonValueKind.String)
        {
            var value = path.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        return null;
    }
}
