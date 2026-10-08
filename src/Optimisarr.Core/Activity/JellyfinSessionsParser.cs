using System.Text.Json;

namespace Optimisarr.Core.Activity;

/// <summary>
/// Parses the <c>/Sessions</c> response shared by Jellyfin and Emby (both descend
/// from MediaBrowser): a JSON array of session objects. A session counts as active
/// playback only when it carries a <c>NowPlayingItem</c>, so idle/connected clients
/// do not pause the queue. Pure and tested against captured payloads.
/// </summary>
public static class JellyfinSessionsParser
{
    public static int ParseActiveSessions(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return 0;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return 0;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return 0;
            }

            var active = 0;
            foreach (var session in document.RootElement.EnumerateArray())
            {
                if (session.ValueKind == JsonValueKind.Object
                    && session.TryGetProperty("NowPlayingItem", out var nowPlaying)
                    && nowPlaying.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                {
                    active++;
                }
            }

            return active;
        }
    }

    /// <summary>
    /// What each playing session is playing and who is watching, from its <c>NowPlayingItem</c>,
    /// <c>UserName</c>, <c>DeviceName</c> (or <c>Client</c>) and <c>PlayState.IsPaused</c>.
    /// Connected clients with nothing playing are skipped, as they are when counting.
    /// </summary>
    public static IReadOnlyList<PlaybackSession> ParseSessions(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return [];
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var sessions = new List<PlaybackSession>();
            foreach (var session in document.RootElement.EnumerateArray())
            {
                if (session.ValueKind == JsonValueKind.Object
                    && session.TryGetProperty("NowPlayingItem", out var item)
                    && item.ValueKind == JsonValueKind.Object)
                {
                    sessions.Add(ParseSession(session, item));
                }
            }

            return sessions;
        }
    }

    private static PlaybackSession ParseSession(JsonElement session, JsonElement item)
    {
        var kind = Text(item, "Type") switch
        {
            "Episode" => PlaybackKind.Episode,
            "Movie" => PlaybackKind.Movie,
            "Audio" => PlaybackKind.Track,
            _ => PlaybackKind.Other,
        };
        var artist = item.TryGetProperty("Artists", out var artists) && artists.ValueKind == JsonValueKind.Array
            ? artists.EnumerateArray().Select(name => name.ValueKind == JsonValueKind.String ? name.GetString() : null)
                .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
            : null;
        var paused = session.TryGetProperty("PlayState", out var state) && state.ValueKind == JsonValueKind.Object
            && state.TryGetProperty("IsPaused", out var isPaused) && isPaused.ValueKind == JsonValueKind.True;
        return new PlaybackSession(
            kind,
            Text(item, "Name"),
            kind == PlaybackKind.Episode ? Text(item, "SeriesName") : null,
            kind == PlaybackKind.Episode ? Number(item, "ParentIndexNumber") : null,
            kind == PlaybackKind.Episode ? Number(item, "IndexNumber") : null,
            Number(item, "ProductionYear"),
            kind == PlaybackKind.Track ? artist?.Trim() ?? Text(item, "AlbumArtist") : null,
            Text(session, "UserName"),
            Text(session, "DeviceName") ?? Text(session, "Client"),
            paused);
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    private static int? Number(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number)
            ? number
            : null;
}
