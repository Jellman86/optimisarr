using System.Security.Cryptography;

namespace Optimisarr.Api.Replacement;

internal static class FileContentIdentity
{
    // Deny ordinary writers while allowing our rename. Unix sharing is not an exclusive
    // filesystem guarantee, so callers also verify the paths after their moves.
    public static FileStream OpenGuard(string path) => new(path, FileMode.Open, FileAccess.Read,
        FileShare.Read | FileShare.Delete, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

    public static bool IsHash(string? hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);

    public static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var stream = OpenGuard(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).ToLowerInvariant();
    }

    public static async Task<bool> MatchesAsync(string path, string? expected, CancellationToken token) =>
        IsHash(expected) && string.Equals(await HashAsync(path, token), expected, StringComparison.OrdinalIgnoreCase);

    public static FileStream OpenAttemptGuard(string path, string detail)
    {
        try { return OpenGuard(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new AttemptContentChangedException(detail); }
    }

    public static async Task<string> HashAttemptAsync(string path, string detail, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { return await HashAsync(path, token); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new AttemptContentChangedException(detail); }
    }

    public static async Task RequireMatchAsync(string path, string? expected, string detail, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!IsHash(expected) || !string.Equals(await HashAttemptAsync(path, detail, token), expected, StringComparison.OrdinalIgnoreCase))
            throw new AttemptContentChangedException(detail);
    }
}
