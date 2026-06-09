using System.Text.Json;
using Optimisarr.Core.Domain;

namespace Optimisarr.Core.Activity;

/// <summary>
/// A provider-agnostic description of the HTTP call that tells a media server to
/// re-scan a changed title. Kept free of <c>HttpClient</c> types so it is pure and
/// trivially unit tested; the API layer turns it into a real request.
/// <paramref name="JsonBody"/> being non-null implies an <c>application/json</c> body.
/// </summary>
public sealed record LibraryRefreshRequest(
    string Method,
    string Url,
    IReadOnlyDictionary<string, string> Headers,
    string? JsonBody);

/// <summary>
/// Builds the per-provider request to refresh a server after a replacement. A
/// replaced file keeps its directory but may change container/codec/size, so the
/// server must re-read it. Plex refreshes its sections; Jellyfin and Emby (shared
/// MediaBrowser lineage) are notified of the changed folder so the scan is targeted.
/// </summary>
public static class LibraryRefreshRequestBuilder
{
    public static LibraryRefreshRequest Build(
        ActivityWatcherType type,
        string baseUrl,
        string? token,
        string changedFilePath)
    {
        var root = baseUrl.TrimEnd('/');
        var authToken = token ?? string.Empty;

        if (type == ActivityWatcherType.Plex)
        {
            // A full section refresh; the server skips unchanged files, so this is
            // cheap, and it needs no section-id lookup to work everywhere.
            return new LibraryRefreshRequest(
                "GET",
                $"{root}/library/sections/all/refresh",
                new Dictionary<string, string>
                {
                    ["X-Plex-Token"] = authToken,
                    ["Accept"] = "application/xml"
                },
                JsonBody: null);
        }

        // Jellyfin / Emby: report the changed folder so only it is rescanned.
        var changedDirectory = Path.GetDirectoryName(changedFilePath) ?? changedFilePath;
        var body = JsonSerializer.Serialize(new
        {
            Updates = new[]
            {
                new { Path = changedDirectory, UpdateType = "Modified" }
            }
        });

        return new LibraryRefreshRequest(
            "POST",
            $"{root}/Library/Media/Updated",
            new Dictionary<string, string>
            {
                ["Authorization"] = $"MediaBrowser Token=\"{authToken}\"",
                ["X-Emby-Token"] = authToken
            },
            body);
    }
}
