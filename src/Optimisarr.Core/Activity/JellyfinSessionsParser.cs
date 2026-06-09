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
}
