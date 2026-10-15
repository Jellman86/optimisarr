using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Optimisarr.Sidecar.Core.Session;

public sealed record NativeScratchRecovery(int Removed = 0, int Busy = 0, int Deferred = 0, int Invalid = 0,
    string? NextOwner = null, bool ScanLimited = false);

/// <summary>Kernel ownership survives parent death until inherited native writers close their handles.</summary>
public sealed class NativeScratchWorkspace : IDisposable
{
    public const string ControlDirectoryName = ".optimisarr-native";
    internal const string MarkerName = ".optimisarr-owner.json";
    private const string GateName = "root.lock";
    private const int MaximumRecordBytes = 4096;
    private const int MaximumEntries = 128;
    private static readonly JsonSerializerOptions Json = new() { MaxDepth = 8, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private sealed record OwnerRecord(int Version, Guid Owner, string Purpose, int? JobId, Guid? LeaseId,
        string Root, string Control, string Gate, string Record, string Directory, string Marker, string State)
    {
        [JsonIgnore] public string Leaf => Purpose == "job" ? $"native-job-{JobId}-{LeaseId:N}-{Owner:N}" : $"native-capability-{Owner:N}";
    }
    private readonly object _gate = new();
    private readonly string _root;
    private readonly string _recordName;
    private readonly byte[] _recordBytes;
    private FileStream? _owner;
    private bool _cleaned;
    public string DirectoryPath { get; }
    internal string RecordPath => Path.Combine(_root, ControlDirectoryName, _recordName);
    internal byte[] RecordBytes => _recordBytes.ToArray();

    private NativeScratchWorkspace(string root, OwnerRecord record, byte[] bytes, FileStream owner)
    {
        _root = root; _recordName = RecordName(record.Owner); _recordBytes = bytes;
        _owner = owner; DirectoryPath = Path.Combine(root, record.Leaf);
    }

    public static NativeScratchWorkspace CreateJob(string root, int jobId, Guid leaseId)
    {
        if (jobId <= 0 || leaseId == Guid.Empty) throw new ArgumentException("A native workspace requires a positive job and lease identity.");
        return Create(root, "job", jobId, leaseId);
    }
    public static NativeScratchWorkspace CreateCapability(string root) => Create(root, "capability", null, null);

    private static NativeScratchWorkspace Create(string path, string purpose, int? job, Guid? lease)
    {
        using var root = NativeWorkspaceDirectory.OpenRoot(path, create: true);
        using var control = Control(root, create: true);
        using var gate = control.OpenFile(GateName, FileMode.OpenOrCreate, locked: true);
        var id = Guid.NewGuid();
        var owner = control.OpenFile(RecordName(id), FileMode.CreateNew, locked: true, inherit: true);
        try
        {
            var record = new OwnerRecord(1, id, purpose, job, lease, root.Identity.Key, control.Identity.Key,
                NativeWorkspaceDirectory.ReadIdentity(gate.SafeFileHandle).Key,
                NativeWorkspaceDirectory.ReadIdentity(owner.SafeFileHandle).Key, "", "", "preparing");
            Write(owner, JsonSerializer.SerializeToUtf8Bytes(record, Json));
            using var directory = root.OpenDirectory(record.Leaf, create: true);
            using var marker = directory.OpenFile(MarkerName, FileMode.CreateNew);
            record = record with { Directory = directory.Identity.Key, Marker = NativeWorkspaceDirectory.ReadIdentity(marker.SafeFileHandle).Key, State = "active" };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(record, Json);
            Write(marker, bytes); Write(owner, bytes);
            return new(root.FullPath, record, bytes, owner);
        }
        catch { owner.Dispose(); throw; }
    }

    // Release this process's reference first. An inherited child can still veto cleanup.
    public bool Cleanup()
    {
        lock (_gate)
        {
            if (_cleaned) return true;
            _owner?.Dispose(); _owner = null;
            try
            {
                using var root = NativeWorkspaceDirectory.OpenRoot(_root, create: false);
                using var control = Control(root, create: false);
                using var gate = control.OpenFile(GateName, locked: true);
                var result = RecoverOwner(root, control, gate, _recordName, _recordBytes);
                _cleaned = result.Removed == 1;
            }
            catch (Exception error) when (Unavailable(error)) { }
            return _cleaned;
        }
    }

    public void Dispose() { lock (_gate) { _owner?.Dispose(); _owner = null; } }

    public static NativeScratchRecovery Recover(string path, int maximumOwners = 64, string? afterOwner = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (maximumOwners is < 1 or > 256 || afterOwner is not null && !TryOwnerName(afterOwner, out _))
            throw new ArgumentException("Invalid native recovery page.");
        try
        {
            using var root = NativeWorkspaceDirectory.OpenRoot(path, create: false);
            using var control = Control(root, create: false);
            using var gate = control.OpenFile(GateName, locked: true);
            var scanned = control.Names().Select(name => { cancellationToken.ThrowIfCancellationRequested(); return name; }).Take(4097).ToArray();
            // Unknown names stay untouched and cannot become a cursor that the next page refuses.
            var names = scanned.Take(4096).Where(name => TryOwnerName(name, out _))
                .Where(name => afterOwner is null || string.CompareOrdinal(name, afterOwner) > 0).Order(StringComparer.Ordinal)
                .Take(maximumOwners + 1).ToArray();
            var result = new NativeScratchRecovery(ScanLimited: scanned.Length > 4096);
            foreach (var name in names.Take(maximumOwners))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = RecoverOwner(root, control, gate, name, cancellationToken: cancellationToken);
                result = result with { Removed = result.Removed + item.Removed, Busy = result.Busy + item.Busy,
                    Deferred = result.Deferred + item.Deferred, Invalid = result.Invalid + item.Invalid };
            }
            return result with { NextOwner = names.Length > maximumOwners ? names[maximumOwners - 1] : null };
        }
        catch (Exception error) when (Missing(error)) { return new(); }
        catch (Exception error) when (Contended(error)) { return new(Busy: 1); }
        catch (Exception error) when (Unavailable(error)) { return new(Deferred: 1); }
    }

    private static NativeScratchRecovery RecoverOwner(NativeWorkspaceDirectory root, NativeWorkspaceDirectory control,
        FileStream gate, string name, byte[]? expected = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryOwnerName(name, out var id)) return new(Invalid: 1);
        FileStream owner;
        try { owner = control.OpenFile(name, locked: true); }
        catch (Exception error) when (Contended(error)) { return new(Busy: 1); }
        catch (Exception error) when (Unavailable(error)) { return new(Invalid: 1); }
        using (owner)
        {
            OwnerRecord record;
            NativeWorkspaceIdentity identity;
            try
            {
                identity = NativeWorkspaceDirectory.ReadIdentity(owner.SafeFileHandle);
                var bytes = Read(owner);
                record = JsonSerializer.Deserialize<OwnerRecord>(bytes, Json) ?? throw new IOException("Missing native owner record.");
                if (!Valid(record) || record.Owner != id || record.Root != root.Identity.Key || record.Control != control.Identity.Key
                    || record.Gate != NativeWorkspaceDirectory.ReadIdentity(gate.SafeFileHandle).Key || record.Record != identity.Key
                    || !bytes.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(record, Json))
                    || expected is not null && !JsonSerializer.SerializeToUtf8Bytes(record with { State = "active" }, Json).AsSpan().SequenceEqual(expected)) return new(Invalid: 1);
                RemoveWork(root, record, owner, cancellationToken);
            }
            catch (Exception error) when (Missing(error))
            {
                return new(Deferred: 1);
            }
            catch (Exception error) when (Unavailable(error) || error is JsonException or DecoderFallbackException) { return new(Deferred: 1); }
            cancellationToken.ThrowIfCancellationRequested();
            owner.Dispose();
            try { control.DeleteFile(name, identity); return new(Removed: 1); }
            catch (Exception error) when (Unavailable(error)) { return new(Deferred: 1); }
        }
    }

    private static void RemoveWork(NativeWorkspaceDirectory root, OwnerRecord record, FileStream owner, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        NativeWorkspaceDirectory directory;
        try { directory = root.OpenDirectory(record.Leaf); }
        catch (Exception error) when (record.State == "deleting" && Missing(error)) { return; }
        using (directory)
        {
            if (directory.Identity.Key != record.Directory) throw new IOException("Native scratch directory ownership changed.");
            try
            {
                using var marker = directory.OpenFile(MarkerName);
                if (NativeWorkspaceDirectory.ReadIdentity(marker.SafeFileHandle).Key != record.Marker
                    || !Read(marker).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(record with { State = "active" }, Json)))
                    throw new IOException("Native scratch ownership marker changed.");
            }
            catch (Exception error) when (record.State == "deleting" && Missing(error)) { }
            using var tree = OwnedTree.Inspect(directory, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (record.State == "active") Write(owner, JsonSerializer.SerializeToUtf8Bytes(record with { State = "deleting" }, Json));
            tree.Delete(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            root.DeleteDirectory(record.Leaf, directory);
        }
    }

    private static bool Valid(OwnerRecord value) => value.Version == 1 && value.Owner != Guid.Empty && value.State is "active" or "deleting"
        && (value.Purpose == "job" && value.JobId is > 0 && value.LeaseId is { } lease && lease != Guid.Empty
            || value.Purpose == "capability" && value.JobId is null && value.LeaseId is null)
        && new[] { value.Root, value.Control, value.Gate, value.Record, value.Directory, value.Marker }.All(x => !string.IsNullOrEmpty(x));
    private static string RecordName(Guid owner) => $"owner-{owner:N}.json";
    private static bool TryOwnerName(string name, out Guid owner) => Guid.TryParseExact(name.StartsWith("owner-", StringComparison.Ordinal)
        && name.EndsWith(".json", StringComparison.Ordinal) ? name[6..^5] : "", "N", out owner) && owner != Guid.Empty;
    private static NativeWorkspaceDirectory Control(NativeWorkspaceDirectory root, bool create)
    {
        try { return root.OpenDirectory(ControlDirectoryName); }
        catch (Exception error) when (create && Missing(error))
        {
            try { return root.OpenDirectory(ControlDirectoryName, create: true); }
            catch (Exception raced) when (raced.InnerException is Win32Exception { NativeErrorCode: 17 or 183 })
            { return root.OpenDirectory(ControlDirectoryName); }
        }
    }
    private static byte[] Read(FileStream file)
    {
        if (file.Length is <= 0 or > MaximumRecordBytes) throw new IOException("Native ownership record exceeds its bounds.");
        file.Position = 0; var bytes = new byte[file.Length]; file.ReadExactly(bytes); return bytes;
    }
    private static void Write(FileStream file, byte[] bytes) { file.Position = 0; file.SetLength(0); file.Write(bytes); file.Flush(flushToDisk: true); }
    private static bool Unavailable(Exception error) => error is IOException or UnauthorizedAccessException or Win32Exception or PlatformNotSupportedException or DecoderFallbackException;
    private static bool Missing(Exception error) => error is DirectoryNotFoundException or FileNotFoundException
        || error.InnerException is Win32Exception { NativeErrorCode: 2 or 3 };
    private static bool Contended(Exception error) => error.InnerException is Win32Exception { NativeErrorCode: 11 or 32 or 33 or 35 };

    private sealed class OwnedTree : IDisposable
    {
        private readonly NativeWorkspaceDirectory _directory;
        private readonly List<(string Name, FileStream File, NativeWorkspaceIdentity Identity)> _files = [];
        private readonly List<(string Name, NativeWorkspaceDirectory Directory, OwnedTree Tree)> _children = [];
        private OwnedTree(NativeWorkspaceDirectory directory) => _directory = directory;
        public static OwnedTree Inspect(NativeWorkspaceDirectory directory, CancellationToken token) { var entries = 0; return Inspect(directory, 0, ref entries, token); }
        private static OwnedTree Inspect(NativeWorkspaceDirectory directory, int depth, ref int entries, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (depth > 16) throw new IOException("Native scratch tree exceeds its depth budget.");
            var tree = new OwnedTree(directory);
            try
            {
                foreach (var name in directory.Names())
                {
                    token.ThrowIfCancellationRequested();
                    if (++entries > MaximumEntries) throw new IOException("Native scratch tree exceeds its entry budget.");
                    FileStream file;
                    try { file = directory.OpenFile(name); }
                    catch (IOException error) when (error is NativeWorkspaceIsDirectoryException || error.InnerException is Win32Exception { NativeErrorCode: 21 or 5 })
                    {
                        var child = directory.OpenDirectory(name);
                        try { tree._children.Add((name, child, Inspect(child, depth + 1, ref entries, token))); }
                        catch { child.Dispose(); throw; }
                        continue;
                    }
                    tree._files.Add((name, file, NativeWorkspaceDirectory.ReadIdentity(file.SafeFileHandle)));
                }
                return tree;
            }
            catch { tree.Dispose(); throw; }
        }
        public void Delete(CancellationToken token)
        {
            foreach (var entry in _files.Where(x => x.Name != MarkerName)) { token.ThrowIfCancellationRequested(); _directory.DeleteFile(entry.Name, entry.Identity, entry.File.SafeFileHandle); entry.File.Dispose(); }
            foreach (var child in _children) { child.Tree.Delete(token); token.ThrowIfCancellationRequested(); _directory.DeleteDirectory(child.Name, child.Directory); child.Directory.Dispose(); }
            foreach (var entry in _files.Where(x => x.Name == MarkerName)) { token.ThrowIfCancellationRequested(); _directory.DeleteFile(entry.Name, entry.Identity, entry.File.SafeFileHandle); entry.File.Dispose(); }
        }
        public void Dispose()
        {
            foreach (var entry in _files) entry.File.Dispose();
            foreach (var child in _children) { child.Tree.Dispose(); child.Directory.Dispose(); }
        }
    }
}
