using System.Security.Cryptography;
using System.Text;
using Optimisarr.Api.Library;

namespace Optimisarr.Api.Deduplication;

/// <summary>All-or-nothing, process-wide claims. Keys include inventory IDs, paths and physical identities.</summary>
public sealed class FileOperationClaims
{
    private readonly object _sync = new();
    private readonly HashSet<string> _held = new(StringComparer.Ordinal);

    public IDisposable? TryAcquire(IEnumerable<string> keys)
    {
        var ordered = keys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (ordered.Length == 0 || ordered.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("A file claim must have nonempty keys.", nameof(keys));
        lock (_sync)
        {
            if (ordered.Any(_held.Contains)) return null;
            foreach (var key in ordered) _held.Add(key);
            return new Claim(this, ordered);
        }
    }

    public static IReadOnlyList<string> Keys(int mediaFileId, params string[] paths)
    {
        var keys = new List<string> { $"media:{mediaFileId}" };
        foreach (var path in paths.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            keys.AddRange(SnapshotKeys(mediaFileId, path, ExactFileIdentity.Read(path)));
        }
        return keys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    public static IReadOnlyList<string> SnapshotKeys(int mediaFileId, string path, string? identity)
    {
        var canonical = Path.GetFullPath(path);
        // Case folding is conservative on potentially case-insensitive filesystems.
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) canonical = canonical.ToUpperInvariant();
        return identity is null
            ? [$"media:{mediaFileId}", "path:" + Digest(canonical)]
            : [$"media:{mediaFileId}", "path:" + Digest(canonical), "identity:" + Digest(identity)];
    }

    private static string Digest(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed class Claim(FileOperationClaims owner, string[] keys) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (owner._sync) foreach (var key in keys) owner._held.Remove(key);
        }
    }
}
