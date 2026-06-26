using System.Text.Json;

namespace Optimisarr.Core.Activity;

/// <summary>
/// Picks a poster image URL from a Radarr movie list or Sonarr series list. Pure JSON parsing (no
/// HTTP) so it is unit tested; the API layer fetches the list and proxies the chosen image. Radarr
/// and Sonarr already hold the artwork for the files they manage, so this is an exact, local source
/// — matched on a normalised title and, when known, the year — rather than a fuzzy media-server
/// search. Prefers the poster's <c>remoteUrl</c> (a public TMDB/TVDB CDN URL needing no API key).
/// </summary>
public static class ArrArtworkParser
{
    /// <summary>
    /// The poster <c>remoteUrl</c> for the title in a Radarr <c>/api/v3/movie</c> or Sonarr
    /// <c>/api/v3/series</c> response (both are arrays of items carrying <c>title</c>, <c>year</c>,
    /// and an <c>images</c> array). Returns an exact title+year match in preference to a title-only
    /// match, and null when nothing matches or the match has no poster.
    /// </summary>
    public static string? PosterRemoteUrl(string? json, string? title, int? year)
    {
        if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var wanted = Normalise(title);
            string? titleOnlyMatch = null;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (Normalise(GetString(item, "title")) != wanted)
                {
                    continue;
                }

                var poster = PosterUrl(item);
                if (poster is null)
                {
                    continue;
                }

                if (year is { } wantedYear && GetInt(item, "year") == wantedYear)
                {
                    return poster;
                }

                titleOnlyMatch ??= poster;
            }

            return titleOnlyMatch;
        }
    }

    private static string? PosterUrl(JsonElement item)
    {
        if (!item.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var image in images.EnumerateArray())
        {
            if (GetString(image, "coverType") == "poster")
            {
                var remote = GetString(image, "remoteUrl");
                if (!string.IsNullOrEmpty(remote))
                {
                    return remote;
                }
            }
        }

        return null;
    }

    // Compare titles ignoring case, punctuation, and spacing so "Godzilla x Kong: The New Empire"
    // matches "Godzilla x Kong - The New Empire".
    private static string Normalise(string? value) =>
        value is null
            ? string.Empty
            : new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? GetInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
}
