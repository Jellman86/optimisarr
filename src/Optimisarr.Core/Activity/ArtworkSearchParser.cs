using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Optimisarr.Core.Activity;

/// <summary>
/// Picks an artwork path from a media server's search response. Pure JSON parsing (no HTTP), so it
/// is unit tested; the API layer makes the request and proxies the chosen image.
///
/// <para>Media-server search is fuzzy — "The Odyssey" also finds "Homer's Odyssey", an episode of
/// The Simpsons — so a result is only used when it is the same title, of the right kind (film vs
/// show) and, when both years are known, from the same year give or take one. Artwork exists to
/// help someone recognise a title; another title's artwork does the opposite, so no match means no
/// artwork rather than the nearest picture.</para>
/// </summary>
public static partial class ArtworkSearchParser
{
    /// <summary>
    /// From a Plex search response, the relative <c>art</c> (backdrop) path of the matching title,
    /// e.g. <c>/library/metadata/4492/art/1782109387</c>. Handles both the legacy <c>/search</c>
    /// shape (results under <c>MediaContainer.Metadata</c>) and the <c>/hubs/search</c> shape
    /// (results grouped under <c>MediaContainer.Hub[].Metadata</c>).
    /// </summary>
    public static string? PlexArtPath(string? json, string title, bool isTv, int? year) =>
        BestPlexMatch(json, title, isTv, year, item => GetString(item, "grandparentArt") ?? GetString(item, "art"));

    /// <summary>
    /// From a Plex search response, the portrait poster of the matching title, e.g.
    /// <c>/library/metadata/4492/thumb/1782109387</c>. An episode's own thumb is a still from the
    /// episode, so a show found through an episode or season uses the show's poster instead.
    /// </summary>
    public static string? PlexPosterPath(string? json, string title, bool isTv, int? year) =>
        BestPlexMatch(json, title, isTv, year, item => GetString(item, "type") switch
        {
            "episode" => GetString(item, "grandparentThumb"),
            "season" => GetString(item, "parentThumb"),
            _ => GetString(item, "thumb"),
        });

    /// <summary>
    /// From a Jellyfin/Emby <c>/Items</c> search, the backdrop path of the matching title, e.g.
    /// <c>/Items/{id}/Images/Backdrop/0?tag=...</c>.
    /// </summary>
    public static string? JellyfinBackdropPath(string? json, string title, bool isTv, int? year) =>
        BestJellyfinMatch(json, title, isTv, year, (id, item) =>
            item.TryGetProperty("BackdropImageTags", out var tags)
            && tags.ValueKind == JsonValueKind.Array
            && tags.GetArrayLength() > 0
            && tags[0].GetString() is { Length: > 0 } tag
                ? $"/Items/{id}/Images/Backdrop/0?tag={tag}"
                : null);

    /// <summary>
    /// From a Jellyfin/Emby <c>/Items</c> search, the primary-image (poster) path of the matching
    /// title, e.g. <c>/Items/{id}/Images/Primary?tag=...</c>.
    /// </summary>
    public static string? JellyfinPosterPath(string? json, string title, bool isTv, int? year) =>
        BestJellyfinMatch(json, title, isTv, year, (id, item) =>
            item.TryGetProperty("ImageTags", out var imageTags)
            && imageTags.ValueKind == JsonValueKind.Object
            && GetString(imageTags, "Primary") is { Length: > 0 } tag
                ? $"/Items/{id}/Images/Primary?tag={tag}"
                : null);

    private static string? BestPlexMatch(
        string? json, string title, bool isTv, int? year, Func<JsonElement, string?> image)
    {
        if (TryRoot(json) is not { } root || !root.TryGetProperty("MediaContainer", out var container))
        {
            return null;
        }

        var wanted = NormaliseTitle(title);
        string? sameYear = null;
        string? sameTitle = null;
        foreach (var item in PlexMetadataItems(container))
        {
            var type = GetString(item, "type") ?? "";
            if (!(isTv ? type is "show" or "season" or "episode" : type == "movie"))
            {
                continue;
            }

            // A season or episode names its show one or two levels up.
            var name = type switch
            {
                "episode" => GetString(item, "grandparentTitle"),
                "season" => GetString(item, "parentTitle"),
                _ => GetString(item, "title"),
            };
            if (name is null || NormaliseTitle(name) != wanted || image(item) is not { Length: > 0 } path)
            {
                continue;
            }

            // An episode's year is when it aired, not when the show began, so only a film or a
            // show's own year can confirm or rule out a match.
            var itemYear = type is "movie" or "show" ? GetInt(item, "year") : null;
            switch (YearFit(year, itemYear))
            {
                case Fit.Exact: sameYear ??= path; break;
                case Fit.Plausible: sameTitle ??= path; break;
            }
        }

        return sameYear ?? sameTitle;
    }

    private static string? BestJellyfinMatch(
        string? json, string title, bool isTv, int? year, Func<string, JsonElement, string?> image)
    {
        if (TryRoot(json) is not { } root
            || !root.TryGetProperty("Items", out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var wanted = NormaliseTitle(title);
        string? sameYear = null;
        string? sameTitle = null;
        foreach (var item in items.EnumerateArray())
        {
            var type = GetString(item, "Type") ?? "";
            if (!(isTv ? type == "Series" : type == "Movie")
                || GetString(item, "Name") is not { } name
                || NormaliseTitle(name) != wanted
                || GetString(item, "Id") is not { Length: > 0 } id
                || image(id, item) is not { } path)
            {
                continue;
            }

            switch (YearFit(year, GetInt(item, "ProductionYear")))
            {
                case Fit.Exact: sameYear ??= path; break;
                case Fit.Plausible: sameTitle ??= path; break;
            }
        }

        return sameYear ?? sameTitle;
    }

    private enum Fit { Exact, Plausible, Wrong }

    // A file's year and a server's year often disagree by one (festival vs release year), so one
    // either side is still the same title. Further apart it is a remake or a namesake.
    private static Fit YearFit(int? wanted, int? listed) =>
        wanted is not { } w || listed is not { } l ? Fit.Plausible
        : w == l ? Fit.Exact
        : Math.Abs(w - l) <= 1 ? Fit.Plausible
        : Fit.Wrong;

    /// <summary>
    /// A title reduced to its letters and digits, so "Marvel’s Agents of S.H.I.E.L.D.", "Law &amp;
    /// Order", "Pokémon" and "Bluey (2018)" compare equal to how a library folder spells them.
    /// </summary>
    public static string NormaliseTitle(string title)
    {
        // Decomposing splits an accented letter into its base letter and a mark; keeping only
        // letters and digits then drops the mark, the punctuation and the spaces together.
        var decomposed = TrailingYear().Replace(title, "").Replace("&", " and ").Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    [GeneratedRegex(@"\s*\((?:19|20)\d{2}\)\s*$")]
    private static partial Regex TrailingYear();

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
