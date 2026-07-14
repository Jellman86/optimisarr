using System.Security.Cryptography;
using System.Text;

namespace Optimisarr.Api.Security;

public static class AdminTokenAuth
{
    public const string EnvironmentVariable = "OPTIMISARR_ADMIN_TOKEN";
    public const string SessionCookie = "optimisarr_admin_session";

    public static bool Required(string? configuredToken) => !string.IsNullOrWhiteSpace(configuredToken);

    public static bool IsOpenPath(PathString path) =>
        path.Equals("/api/health", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/api/ready", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/api/auth/status", StringComparison.OrdinalIgnoreCase);

    public static bool IsProtectedPath(PathString path) =>
        path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/hubs", StringComparison.OrdinalIgnoreCase);

    public static bool IsAuthorized(HttpRequest request, string configuredToken)
    {
        var bearer = TokenFromAuthorizationHeader(request);
        if (bearer is not null)
        {
            return FixedTimeEquals(bearer, configuredToken);
        }

        var hubQuery = TokenFromHubQueryString(request);
        if (hubQuery is not null)
        {
            return FixedTimeEquals(hubQuery, configuredToken);
        }

        return request.Cookies.TryGetValue(SessionCookie, out var session)
            && FixedTimeEquals(session, SessionValue(configuredToken));
    }

    public static bool HasBearerToken(HttpRequest request) =>
        TokenFromAuthorizationHeader(request) is not null;

    public static void SetSessionCookie(HttpResponse response, string configuredToken, bool secure)
    {
        response.Cookies.Append(
            SessionCookie,
            SessionValue(configuredToken),
            new CookieOptions
            {
                HttpOnly = true,
                Secure = secure,
                SameSite = SameSiteMode.Strict,
                Path = "/"
            });
    }

    private static string? TokenFromAuthorizationHeader(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        return header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? header[prefix.Length..].Trim()
            : null;
    }

    private static string? TokenFromHubQueryString(HttpRequest request)
    {
        if (request.Path.StartsWithSegments("/hubs", StringComparison.OrdinalIgnoreCase)
            && request.Query.TryGetValue("access_token", out var token)
            && !string.IsNullOrWhiteSpace(token))
        {
            return token.ToString();
        }

        return null;
    }

    private static bool FixedTimeEquals(string supplied, string expected)
    {
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(suppliedHash, expectedHash);
    }

    private static string SessionValue(string configuredToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(configuredToken)));
}
