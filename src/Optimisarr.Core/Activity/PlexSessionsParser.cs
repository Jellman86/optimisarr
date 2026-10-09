using System.Xml.Linq;

namespace Optimisarr.Core.Activity;

/// <summary>
/// Parses Plex's <c>/status/sessions</c> response, which is an XML
/// <c>&lt;MediaContainer&gt;</c> whose children are the in-progress playback
/// sessions. Pure and tested against captured payloads — no HTTP here.
/// </summary>
public static class PlexSessionsParser
{
    public static int ParseActiveSessions(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return 0;
        }

        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            return 0;
        }

        var container = document.Root;
        if (container is null)
        {
            return 0;
        }

        // The element children (Video, Track, Photo…) are the live sessions. Prefer
        // counting them; fall back to the advertised "size" attribute if there are
        // none (an empty container still carries size="0").
        var sessions = container.Elements().Count();
        if (sessions > 0)
        {
            return sessions;
        }

        return int.TryParse(container.Attribute("size")?.Value, out var size) && size > 0
            ? size
            : 0;
    }

    /// <summary>
    /// What each live session is playing and who is watching. An episode's own title is
    /// <c>title</c>, its show <c>grandparentTitle</c>, season <c>parentIndex</c> and episode
    /// <c>index</c>; a track's artist is <c>originalTitle</c>, else <c>grandparentTitle</c>. The viewer is the child
    /// <c>User@title</c> and the device <c>Player@title</c>, whose <c>state</c> says paused.
    /// </summary>
    public static IReadOnlyList<PlaybackSession> ParseSessions(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return [];
        }

        XElement? container;
        try
        {
            container = XDocument.Parse(xml).Root;
        }
        catch (System.Xml.XmlException)
        {
            return [];
        }

        return container is null ? [] : container.Elements().Select(ParseSession).ToList();
    }

    private static PlaybackSession ParseSession(XElement session)
    {
        var kind = (session.Name.LocalName, (string?)session.Attribute("type")) switch
        {
            (_, "episode") => PlaybackKind.Episode,
            (_, "movie") => PlaybackKind.Movie,
            ("Track", _) or (_, "track") => PlaybackKind.Track,
            _ => PlaybackKind.Other,
        };
        var player = session.Element("Player");
        return new PlaybackSession(
            kind,
            Text(session.Attribute("title")),
            kind == PlaybackKind.Episode ? Text(session.Attribute("grandparentTitle")) : null,
            kind == PlaybackKind.Episode ? Number(session.Attribute("parentIndex")) : null,
            kind == PlaybackKind.Episode ? Number(session.Attribute("index")) : null,
            Number(session.Attribute("year")),
            // A compilation's grandparentTitle is the album artist; originalTitle is the track's own.
            kind == PlaybackKind.Track ? Text(session.Attribute("originalTitle")) ?? Text(session.Attribute("grandparentTitle")) : null,
            Text(session.Element("User")?.Attribute("title")),
            Text(player?.Attribute("title")),
            string.Equals((string?)player?.Attribute("state"), "paused", StringComparison.OrdinalIgnoreCase),
            Artwork(session, kind));
    }

    private static PlaybackArtwork? Artwork(XElement session, PlaybackKind kind) =>
        (kind switch
        {
            PlaybackKind.Episode => PlaybackArtwork.Plex(Text(session.Attribute("grandparentThumb")))
                ?? PlaybackArtwork.Plex(Text(session.Attribute("parentThumb"))),
            PlaybackKind.Track => PlaybackArtwork.Plex(Text(session.Attribute("parentThumb"))),
            _ => null,
        }) ?? PlaybackArtwork.Plex(Text(session.Attribute("thumb")));

    private static string? Text(XAttribute? attribute) =>
        string.IsNullOrWhiteSpace(attribute?.Value) ? null : attribute.Value.Trim();

    private static int? Number(XAttribute? attribute) =>
        int.TryParse(attribute?.Value, out var value) ? value : null;
}
