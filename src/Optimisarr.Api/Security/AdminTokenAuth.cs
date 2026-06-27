using System.Security.Cryptography;
using System.Text;

namespace Optimisarr.Api.Security;

public static class AdminTokenAuth
{
    public const string EnvironmentVariable = "OPTIMISARR_ADMIN_TOKEN";

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
        var supplied = TokenFromAuthorizationHeader(request)
            ?? TokenFromQueryString(request);

        return supplied is not null && FixedTimeEquals(supplied, configuredToken);
    }

    private static string? TokenFromAuthorizationHeader(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        return header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? header[prefix.Length..].Trim()
            : null;
    }

    private static string? TokenFromQueryString(HttpRequest request)
    {
        if (request.Query.TryGetValue("access_token", out var token)
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
}
