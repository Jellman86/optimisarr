using Optimisarr.Api.Library;
using Optimisarr.Core.Deduplication;
using Optimisarr.Data;

namespace Optimisarr.Api.Deduplication;

/// <summary>All-or-nothing, process-wide claims. Keys include inventory IDs, paths and physical identities.</summary>
public sealed class FileOperationClaims : IFileReservationKeys
{
    IReadOnlyList<string> IFileReservationKeys.Resolve(int mediaFileId, params string[] paths) => Keys(mediaFileId, paths);
    private readonly object _sync = new();
    private readonly HashSet<string> _held = new(StringComparer.Ordinal);
    private bool _recursiveCleanup;

    public IDisposable? TryAcquire(IEnumerable<string> keys)
    {
        var ordered = keys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (ordered.Length == 0 || ordered.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("A file claim must have nonempty keys.", nameof(keys));
        lock (_sync)
        {
            if (_recursiveCleanup || ordered.Any(_held.Contains)) return null;
            foreach (var key in ordered) _held.Add(key);
            return new Claim(this, ordered);
        }
    }

    internal IDisposable? TryAcquireRecursiveCleanup()
    {
        lock (_sync)
        {
            if (_recursiveCleanup || _held.Count != 0) return null;
            _recursiveCleanup = true;
            return new RecursiveCleanupClaim(this);
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
        var key = FileClaimKeys.Path(canonical, OperatingSystem.IsWindows() || OperatingSystem.IsMacOS());
        return identity is null
            ? [FileClaimKeys.Media(mediaFileId), key]
            : [FileClaimKeys.Media(mediaFileId), key, FileClaimKeys.Identity(identity)];
    }

    private sealed class Claim(FileOperationClaims owner, string[] keys) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (owner._sync) foreach (var key in keys) owner._held.Remove(key);
        }
    }

    private sealed class RecursiveCleanupClaim(FileOperationClaims owner) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (owner._sync) owner._recursiveCleanup = false;
        }
    }
}
