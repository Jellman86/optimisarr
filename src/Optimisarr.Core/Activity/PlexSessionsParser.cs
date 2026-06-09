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
}
