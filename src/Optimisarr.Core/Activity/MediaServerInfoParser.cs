using System.Text.Json;

namespace Optimisarr.Core.Activity;

/// <summary>Identity returned by a successful connection test: the server's name and version.</summary>
public sealed record MediaServerInfo(string Name, string? Version);

/// <summary>
/// Parses the small identity payloads used to confirm a media-server connection works: Plex's root
/// (<c>GET /</c> with <c>Accept: application/json</c>) and Jellyfin/Emby's <c>GET /System/Info</c>.
/// Pure so the "Test connection" logic is unit tested without a live server.
/// </summary>
public static class MediaServerInfoParser
{
    /// <summary>Plex root: <c>{ "MediaContainer": { "friendlyName": "...", "version": "..." } }</c>.</summary>
    public static MediaServerInfo? ParsePlex(string? json)
    {
        var root = TryParse(json);
        if (root is not { } element)
        {
            return null;
        }

        if (!element.TryGetProperty("MediaContainer", out var container) || container.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var name = GetString(container, "friendlyName");
        return name is null ? null : new MediaServerInfo(name, GetString(container, "version"));
    }

    /// <summary>Jellyfin/Emby <c>/System/Info</c>: <c>{ "ServerName": "...", "Version": "..." }</c>.</summary>
    public static MediaServerInfo? ParseJellyfin(string? json)
    {
        var root = TryParse(json);
        if (root is not { } element || element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var name = GetString(element, "ServerName");
        return name is null ? null : new MediaServerInfo(name, GetString(element, "Version"));
    }

    private static JsonElement? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            // Clone so the element stays valid after the JsonDocument is disposed.
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
