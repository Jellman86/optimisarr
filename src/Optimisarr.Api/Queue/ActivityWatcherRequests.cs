using Optimisarr.Core.Domain;
using Optimisarr.Data;

namespace Optimisarr.Api.Queue;

/// <summary>A watcher shaped for the client. The token is never returned — only whether one is set.</summary>
public sealed record ActivityWatcherDto(
    int Id,
    string Name,
    string Type,
    string BaseUrl,
    bool HasToken,
    bool Enabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static ActivityWatcherDto From(ActivityWatcher watcher) => new(
        watcher.Id,
        watcher.Name,
        watcher.Type.ToString(),
        watcher.BaseUrl,
        !string.IsNullOrEmpty(watcher.ApiToken),
        watcher.Enabled,
        watcher.CreatedAt,
        watcher.UpdatedAt);
}

public sealed record SaveActivityWatcherRequest(
    string? Name,
    string? Type,
    string? BaseUrl,
    string? ApiToken,
    bool? Enabled);

public sealed record ParsedActivityWatcher(
    string Name,
    ActivityWatcherType Type,
    string BaseUrl,
    string? ApiToken,
    bool Enabled);

public static class ActivityWatcherRequestParser
{
    /// <summary>
    /// Validates a save request. A blank <c>ApiToken</c> is returned as null so the
    /// caller can decide to keep an existing secret on update rather than wiping it.
    /// </summary>
    public static bool TryParse(
        SaveActivityWatcherRequest request,
        out ParsedActivityWatcher parsed,
        out string? error)
    {
        parsed = null!;
        error = null;

        var name = request.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "Name is required.";
            return false;
        }

        if (!Enum.TryParse<ActivityWatcherType>(request.Type, ignoreCase: true, out var type))
        {
            error = "Type must be one of Plex, Jellyfin, or Emby.";
            return false;
        }

        var baseUrl = request.BaseUrl?.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl)
            || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = "Base URL must be an absolute http(s) URL, e.g. http://192.168.1.10:32400.";
            return false;
        }

        var token = string.IsNullOrWhiteSpace(request.ApiToken) ? null : request.ApiToken.Trim();
        parsed = new ParsedActivityWatcher(name, type, baseUrl, token, request.Enabled ?? true);
        return true;
    }
}
