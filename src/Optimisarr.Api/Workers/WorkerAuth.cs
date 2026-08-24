using Microsoft.EntityFrameworkCore;
using Optimisarr.Core.Workers;
using Optimisarr.Data;

namespace Optimisarr.Api.Workers;

/// <summary>
/// Resolves the paired sidecar behind a request from the credential it presents.
///
/// A worker authenticates with its own credential rather than the admin token: it never has one,
/// and issuing it one would hand a remote machine the keys to the whole API. The credential is 32
/// random bytes, so it is a far stronger secret than an operator-chosen admin token, but it
/// authorises only the worker routes.
/// </summary>
internal static class WorkerAuth
{
    /// <summary>
    /// The worker this request belongs to, or null when the credential is absent, malformed,
    /// unknown, or revoked.
    /// </summary>
    public static async Task<Worker?> ResolveAsync(
        HttpRequest request,
        OptimisarrDbContext db,
        CancellationToken cancellationToken)
    {
        var credential = BearerCredential(request);
        if (string.IsNullOrWhiteSpace(credential))
        {
            return null;
        }

        // Looked up by fingerprint rather than by scanning and comparing every row. The secret is
        // 256 bits of randomness, so an indexed equality lookup on its hash leaks nothing an
        // attacker could use — this is the standard stored-hash token pattern, and the alternative
        // would be a table scan on every heartbeat.
        var fingerprint = WorkerCredential.Fingerprint(credential);

        // A revoked worker's fingerprint is null, so it can never match here. Revocation therefore
        // needs no separate check and cannot be forgotten at a call site.
        var worker = await db.Workers
            .FirstOrDefaultAsync(w => w.CredentialFingerprint == fingerprint, cancellationToken);

        if (worker?.CredentialFingerprint is null)
        {
            return null;
        }

        // Belt and braces: the row was found by exact fingerprint, so this can only agree, but the
        // constant-time comparison keeps one verification path shared with AdminTokenAuth rather
        // than trusting the query alone.
        return WorkerCredential.Matches(credential, worker.CredentialFingerprint) ? worker : null;
    }

    private static string? BearerCredential(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        return header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? header[prefix.Length..].Trim()
            : null;
    }
}
