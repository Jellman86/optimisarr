using System.Text.Json;

namespace Optimisarr.Core.Activity;

/// <summary>A freshly created Plex PIN: its id (used to poll) and the code the user authorises.</summary>
public sealed record PlexPin(long Id, string Code);

/// <summary>
/// Parses the JSON from Plex's <c>/api/v2/pins</c> endpoints. Pure and tested
/// against captured payloads — the HTTP and the OAuth dance live in the API layer.
/// </summary>
public static class PlexPinParser
{
    /// <summary>Parses the id and code from a created PIN, or null if the shape is wrong.</summary>
    public static PlexPin? ParsePin(string json)
    {
        if (!TryRoot(json, out var root) || root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        var code = root.TryGetProperty("code", out var codeEl) ? codeEl.GetString() : null;
        return string.IsNullOrEmpty(code) ? null : new PlexPin(id.GetInt64(), code);
    }

    /// <summary>
    /// Returns the claimed auth token from a polled PIN, or null while it is still
    /// unclaimed (Plex sends <c>authToken: null</c> until the user authorises).
    /// </summary>
    public static string? ParseAuthToken(string json)
    {
        if (!TryRoot(json, out var root) || root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!root.TryGetProperty("authToken", out var token) || token.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = token.GetString();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static bool TryRoot(string json, out JsonElement root)
    {
        root = default;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            root = JsonDocument.Parse(json).RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
