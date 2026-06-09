using Optimisarr.Core.Domain;
using Optimisarr.Data;

namespace Optimisarr.Api.Library;

/// <summary>An arr connection shaped for the client. The API key is never returned — only whether one is set.</summary>
public sealed record ArrConnectionDto(
    int Id,
    string Name,
    string Type,
    string BaseUrl,
    bool HasApiKey,
    bool Enabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static ArrConnectionDto From(ArrConnection connection) => new(
        connection.Id,
        connection.Name,
        connection.Type.ToString(),
        connection.BaseUrl,
        !string.IsNullOrEmpty(connection.ApiKey),
        connection.Enabled,
        connection.CreatedAt,
        connection.UpdatedAt);
}

public sealed record SaveArrConnectionRequest(
    string? Name,
    string? Type,
    string? BaseUrl,
    string? ApiKey,
    bool? Enabled);

public sealed record ParsedArrConnection(
    string Name,
    ArrConnectionType Type,
    string BaseUrl,
    string? ApiKey,
    bool Enabled);

public static class ArrConnectionRequestParser
{
    /// <summary>
    /// Validates a save request. A blank <c>ApiKey</c> is returned as null so the caller
    /// can keep an existing secret on update rather than wiping it.
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
        parsed = new ParsedArrConnection(name, type, baseUrl, apiKey, request.Enabled ?? true);
        return true;
    }
}
