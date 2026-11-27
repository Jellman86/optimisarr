using System.Text;
using Optimisarr.Core.Workers;

namespace Optimisarr.Api.Security;

/// <summary>
/// How a Sonarr/Radarr webhook proves it belongs to a connection. The manager cannot hold the admin
/// token, so each connection has its own password, sent as HTTP basic authentication from the username
/// and password fields every Sonarr and Radarr webhook connection has. Basic auth rather than a secret in
/// the URL, because URLs end up in proxy access logs and the manager shows its URL field in plain text.
/// The password is 32 random bytes, minted the same way as a worker credential, and only its fingerprint
/// is stored, so it is shown once and replaced rather than recovered.
/// </summary>
public static class ArrWebhookAuth
{
    /// <summary>What the instructions suggest typing as the username. Any username is accepted.</summary>
    public const string Username = "optimisarr";

    public const string Realm = "Basic realm=\"Optimisarr webhook\", charset=\"UTF-8\"";

    public const string PathPrefix = "/api/arr-webhooks";

    public static string PathFor(int connectionId) => $"{PathPrefix}/{connectionId}";

    /// <summary>
    /// True only for a delivery route itself, <c>/api/arr-webhooks/&lt;id&gt;</c>: not the bare prefix and
    /// nothing beneath an id, so the admin-token exemption cannot widen to a route added there later.
    /// </summary>
    public static bool IsWebhookPath(PathString path) =>
        path.StartsWithSegments(PathPrefix, StringComparison.OrdinalIgnoreCase, out var rest)
        && rest.Value is { Length: > 1 } id
        && id[0] == '/'
        && !id.AsSpan(1).ContainsAnyExceptInRange('0', '9');

    public static string IssuePassword() => WorkerCredential.Issue();

    public static string Fingerprint(string password) => WorkerCredential.Fingerprint(password);

    /// <summary>True only for a basic-auth password matching the stored fingerprint. No fingerprint matches nothing.</summary>
    public static bool IsAuthorized(HttpRequest request, string? storedFingerprint) =>
        storedFingerprint is not null
        && BasicPassword(request) is { } password
        && WorkerCredential.Matches(password, storedFingerprint);

    private static string? BasicPassword(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        const string prefix = "Basic ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[prefix.Length..].Trim()));
            var colon = decoded.IndexOf(':');
            return colon < 0 ? null : decoded[(colon + 1)..];
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
