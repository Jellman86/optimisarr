using System.Text.Json;

namespace Optimisarr.Core.Activity;

/// <summary>An initiated Jellyfin Quick Connect request: the code to show the user and the secret to poll with.</summary>
public sealed record QuickConnectInitiation(string Code, string Secret);

/// <summary>
/// Parses Jellyfin's Quick Connect JSON (<c>/QuickConnect/Initiate</c>,
/// <c>/QuickConnect/Connect</c>, and the authenticate result). Pure and tested
/// against captured payloads. Property names are PascalCase as Jellyfin emits them.
/// </summary>
public static class JellyfinQuickConnectParser
{
    public static QuickConnectInitiation? ParseInitiation(string json)
    {
        if (!TryRoot(json, out var root) || root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var code = root.TryGetProperty("Code", out var codeEl) ? codeEl.GetString() : null;
        var secret = root.TryGetProperty("Secret", out var secretEl) ? secretEl.GetString() : null;
        return string.IsNullOrEmpty(code) || string.IsNullOrEmpty(secret)
            ? null
            : new QuickConnectInitiation(code, secret);
    }

    /// <summary>True once the user has approved the code from a signed-in Jellyfin session.</summary>
    public static bool ParseAuthenticated(string json)
    {
        if (!TryRoot(json, out var root) || root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return root.TryGetProperty("Authenticated", out var authenticated)
            && authenticated.ValueKind == JsonValueKind.True;
    }

    /// <summary>Returns the access token from the authenticate-with-Quick-Connect result, or null.</summary>
    public static string? ParseAccessToken(string json)
    {
        if (!TryRoot(json, out var root) || root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!root.TryGetProperty("AccessToken", out var token) || token.ValueKind != JsonValueKind.String)
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
