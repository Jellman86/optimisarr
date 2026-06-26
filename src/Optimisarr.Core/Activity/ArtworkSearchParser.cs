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
    /// From a Plex search response, the relative <c>art</c> (backdrop) path of the best match, e.g.
    /// <c>/library/metadata/4492/art/1782109387</c>. Null when nothing has art. Handles both the
    /// legacy <c>/search</c> shape (results directly under <c>MediaContainer.Metadata</c>) and the
    /// <c>/hubs/search</c> shape (results grouped under <c>MediaContainer.Hub[].Metadata</c>) — many
    /// servers' <c>/search</c> returns only providers, so we query <c>/hubs/search</c>.
    /// </summary>
    public static string? PlexArtPath(string? json, bool isTv, int? year)
    {
        var root = TryRoot(json);
        if (root is not { } element
            || !element.TryGetProperty("MediaContainer", out var container))
        {
            return null;
        }

        string? typeMatchYear = null;
        string? typeMatch = null;
        string? anyArt = null;

        foreach (var item in PlexMetadataItems(container))
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

    // Flattens result items from both Plex search shapes: top-level Metadata (/search) and
    // Hub[].Metadata (/hubs/search).
    private static IEnumerable<JsonElement> PlexMetadataItems(JsonElement container)
    {
        if (container.TryGetProperty("Metadata", out var direct) && direct.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in direct.EnumerateArray())
            {
                yield return item;
            }
        }

        if (container.TryGetProperty("Hub", out var hubs) && hubs.ValueKind == JsonValueKind.Array)
        {
            foreach (var hub in hubs.EnumerateArray())
            {
                if (hub.TryGetProperty("Metadata", out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        yield return item;
                    }
                }
            }
        }
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

    /// <summary>
    /// From a Plex search response, the relative <c>thumb</c> (poster) path of the best match, e.g.
    /// <c>/library/metadata/4492/thumb/1782109387</c>. Null when nothing has a thumb. Mirrors
    /// <see cref="PlexArtPath"/> but selects the portrait poster rather than the wide backdrop.
    /// </summary>
    public static string? PlexPosterPath(string? json, bool isTv, int? year)
    {
        var root = TryRoot(json);
        if (root is not { } element || !element.TryGetProperty("MediaContainer", out var container))
        {
            return null;
        }

        string? typeMatchYear = null;
        string? typeMatch = null;
        string? anyThumb = null;

        foreach (var item in PlexMetadataItems(container))
        {
            var thumb = GetString(item, "thumb");
            if (string.IsNullOrEmpty(thumb))
            {
                continue;
            }

            anyThumb ??= thumb;
            var type = GetString(item, "type") ?? "";
            var kindMatches = isTv ? type is "show" or "season" or "episode" : type == "movie";
            if (!kindMatches)
            {
                continue;
            }

            typeMatch ??= thumb;
            if (year is { } y && GetInt(item, "year") == y)
            {
                typeMatchYear ??= thumb;
            }
        }

        return typeMatchYear ?? typeMatch ?? anyThumb;
    }

    /// <summary>
    /// From a Jellyfin/Emby <c>/Items</c> search, the relative primary-image (poster) path of the
    /// best match, e.g. <c>/Items/{id}/Images/Primary?tag=...</c>. Null when nothing has a poster.
    /// </summary>
    public static string? JellyfinPosterPath(string? json, bool isTv, int? year)
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
            if (!item.TryGetProperty("ImageTags", out var imageTags)
                || imageTags.ValueKind != JsonValueKind.Object
                || GetString(imageTags, "Primary") is not { Length: > 0 } tag)
            {
                continue;
            }

            var id = GetString(item, "Id");
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }

            var path = $"/Items/{id}/Images/Primary?tag={tag}";
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
