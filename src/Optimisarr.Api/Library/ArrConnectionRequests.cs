using Optimisarr.Core.Domain;
using Optimisarr.Api.Security;
using Optimisarr.Data;

namespace Optimisarr.Api.Library;

/// <summary>
/// An arr connection shaped for the client. Neither the API key nor the webhook password is returned,
/// only whether each is set.
/// </summary>
public sealed record ArrConnectionDto(
    int Id,
    string Name,
    string Type,
    string BaseUrl,
    bool HasApiKey,
    bool Enabled,
    bool RescanAfterChanges,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool WebhookEnabled,
    string WebhookPath,
    string? RemotePathPrefix,
    string? LocalPathPrefix,
    DateTimeOffset? WebhookLastReceivedAt,
    string? WebhookLastOutcome,
    string? WebhookLastDetail)
{
    public static ArrConnectionDto From(ArrConnection connection) => new(
        connection.Id,
        connection.Name,
        connection.Type.ToString(),
        connection.BaseUrl,
        !string.IsNullOrEmpty(connection.ApiKey),
        connection.Enabled,
        connection.RescanAfterChanges,
        connection.CreatedAt,
        connection.UpdatedAt,
        connection.WebhookSecretFingerprint is not null,
        ArrWebhookAuth.PathFor(connection.Id),
        connection.RemotePathPrefix,
        connection.LocalPathPrefix,
        connection.WebhookLastReceivedAt,
        connection.WebhookLastOutcome?.ToString(),
        connection.WebhookLastDetail);
}

/// <summary>A new webhook password, returned once. Only its fingerprint is stored.</summary>
public sealed record ArrWebhookPasswordDto(string Username, string Password, string WebhookPath);

public sealed record SaveArrConnectionRequest(
    string? Name,
    string? Type,
    string? BaseUrl,
    string? ApiKey,
    bool? Enabled,
    bool? RescanAfterChanges = null,
    string? RemotePathPrefix = null,
    string? LocalPathPrefix = null);

/// <summary>A path mapping to save: both prefixes, or neither to remove it.</summary>
public sealed record ArrPathMappingUpdate(string? RemotePrefix, string? LocalPrefix);

public sealed record ParsedArrConnection(
    string Name,
    ArrConnectionType Type,
    string BaseUrl,
    string? ApiKey,
    bool Enabled,
    bool? RescanAfterChanges);

public static class ArrConnectionRequestParser
{
    public const string PathMappingError =
        "A path mapping needs both folders: where the manager sees the library, and where Optimisarr sees it, each a full path.";

    /// <summary>
    /// Reads the optional path mapping. Null <paramref name="update"/> means the request left it out, so the
    /// saved one stays; two blank prefixes remove it. A half-filled mapping, or one that climbs with
    /// <c>..</c>, is refused rather than guessed at.
    /// </summary>
    public static bool TryParsePathMapping(SaveArrConnectionRequest request, out ArrPathMappingUpdate? update, out string? error)
    {
        update = null;
        error = null;
        if (request.RemotePathPrefix is null && request.LocalPathPrefix is null)
        {
            return true;
        }

        var remote = string.IsNullOrWhiteSpace(request.RemotePathPrefix) ? null : request.RemotePathPrefix.Trim();
        var local = string.IsNullOrWhiteSpace(request.LocalPathPrefix) ? null : request.LocalPathPrefix.Trim();
        if (remote is null && local is null)
        {
            update = new ArrPathMappingUpdate(null, null);
            return true;
        }

        if (remote is null || local is null || !IsAbsoluteManagerPath(remote) || !Path.IsPathFullyQualified(local)
            || Climbs(remote) || Climbs(local) || remote.Length > 1024 || local.Length > 1024)
        {
            error = PathMappingError;
            return false;
        }

        update = new ArrPathMappingUpdate(remote, local);
        return true;
    }

    // The manager may run on another operating system, so its path is judged by its own style.
    private static bool IsAbsoluteManagerPath(string path) =>
        path.StartsWith('/') || path.StartsWith(@"\\", StringComparison.Ordinal)
        || (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/');

    private static bool Climbs(string path) => path.Split('/', '\\').Contains("..");

    /// <summary>
    /// Validates a save request. A blank <c>ApiKey</c> is returned as null so the caller
    /// can keep an existing secret on update rather than wiping it. An omitted <c>RescanAfterChanges</c>
    /// stays null so the caller can apply its own default or keep the saved choice.
    /// </summary>
    public static bool TryParse(SaveArrConnectionRequest request, out ParsedArrConnection parsed, out string? error)
    {
        parsed = null!;
        error = null;

        var name = request.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "Name is required.";
            return false;
        }

        if (!Enum.TryParse<ArrConnectionType>(request.Type, ignoreCase: true, out var type))
        {
            error = "Type must be one of Sonarr or Radarr.";
            return false;
        }

        var baseUrl = request.BaseUrl?.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl)
            || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = "Base URL must be an absolute http(s) URL, e.g. http://192.168.1.10:8989.";
            return false;
        }

        var apiKey = string.IsNullOrWhiteSpace(request.ApiKey) ? null : request.ApiKey.Trim();
        parsed = new ParsedArrConnection(name, type, baseUrl, apiKey, request.Enabled ?? true, request.RescanAfterChanges);
        return true;
    }
}
