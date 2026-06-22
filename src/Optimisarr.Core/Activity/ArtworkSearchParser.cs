using System.Text.Json;

namespace Optimisarr.Core.Activity;

/// <summary>
/// Picks a backdrop image path from a media server's search response. Pure JSON parsing (no HTTP),
/// so it is unit tested; the API layer makes the request and proxies the chosen image. Prefers a
/// result of the right kind (movie vs show) and, when known, a matching year.
/// </summary>
public static class ArtworkSearchParser
{
    /// <summary>
    /// From a Plex <c>/search</c> response, the relative <c>art</c> (backdrop) path of the best
    /// match, e.g. <c>/library/metadata/4492/art/1782109387</c>. Null when nothing has art.
    /// </summary>
    public static string? PlexArtPath(string? json, bool isTv, int? year)
    {
        var root = TryRoot(json);
        if (root is not { } element
            || !element.TryGetProperty("MediaContainer", out var container)
            || !container.TryGetProperty("Metadata", out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? typeMatchYear = null;
        string? typeMatch = null;
        string? anyArt = null;
        foreach (var item in items.EnumerateArray())
        {
            var art = GetString(item, "art");
            if (string.IsNullOrEmpty(art))
            {
                continue;
            }

            anyArt ??= art;
            var type = GetString(item, "type") ?? "";
            var kindMatches = isTv ? type is "show" or "season" or "episode" : type == "movie";
            if (!kindMatches)
            {
                continue;
            }

            typeMatch ??= art;
            if (year is { } y && GetInt(item, "year") == y)
            {
                typeMatchYear ??= art;
            }
        }

        return typeMatchYear ?? typeMatch ?? anyArt;
    }

    /// <summary>
    /// From a Jellyfin <c>/Items</c> search, the relative backdrop path of the best match, e.g.
    /// <c>/Items/{id}/Images/Backdrop/0?tag=...</c>. Null when nothing has a backdrop.
    /// </summary>
    public static string? JellyfinBackdropPath(string? json, bool isTv, int? year)
    {
        var root = TryRoot(json);
        if (root is not { } element
            || !element.TryGetProperty("Items", out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? typeMatchYear = null;
        string? typeMatch = null;
        string? any = null;
        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("BackdropImageTags", out var tags)
                || tags.ValueKind != JsonValueKind.Array
                || tags.GetArrayLength() == 0)
            {
                continue;
            }

            var id = GetString(item, "Id");
            var tag = tags[0].GetString();
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(tag))
            {
                continue;
            }

            var path = $"/Items/{id}/Images/Backdrop/0?tag={tag}";
            any ??= path;
            var type = GetString(item, "Type") ?? "";
            var kindMatches = isTv ? type == "Series" : type == "Movie";
            if (!kindMatches)
            {
                continue;
            }

            typeMatch ??= path;
            if (year is { } y && GetInt(item, "ProductionYear") == y)
            {
                typeMatchYear ??= path;
            }
        }

        return typeMatchYear ?? typeMatch ?? any;
    }

    private static JsonElement? TryRoot(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? GetInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
}
