using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Optimisarr.Sidecar.Core.Session;

/// <summary>Persistent Linux accounting; incomplete provisioning is retained for explicit recovery.</summary>
internal sealed class LinuxNativeWorkJournalStorage : INativeWorkJournalStorage
{
    private const string LedgerName = "ledger.json", CheckpointName = "checkpoint.json", LockName = "coordinator.lock";
    private const int CloseOnExec = 0x80000, NonBlocking = 0x800;
    private static int NoFollow => LinuxNativeOpenFlags.Current.NoFollow;
    private static int DirectoryFlag => LinuxNativeOpenFlags.Current.Directory;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly PrivateDirectory _journal, _authority;
    private readonly FileStream _coordinator;
    private readonly Identity _lockIdentity;
    private readonly Guid _storeId;
    private readonly byte[] _header;
    private readonly Action<string>? _step;
    private int _disposed;
    public NativeWorkBinding Binding { get; }

    private LinuxNativeWorkJournalStorage(PrivateDirectory journal, PrivateDirectory authority, FileStream coordinator,
        Guid storeId, Action<string>? step)
    {
        _journal = journal; _authority = authority; _coordinator = coordinator; _storeId = storeId;
        _lockIdentity = Inspect(coordinator.SafeFileHandle, directory: false, privateOwner: true);
        _header = Header(storeId); _step = step;
        Binding = new(journal.Path, journal.Identity.Key, _lockIdentity.Key + ":" + storeId.ToString("N"));
        if (journal.Identity.Key == authority.Identity.Key) throw new IOException("Native checkpoint storage must be physically independent of the journal directory.");
    }

    public static LinuxNativeWorkJournalStorage ProvisionNew(string journalPath, string checkpointPath, Action<string>? step = null)
    {
        var (journalName, authorityName) = Paths(journalPath, checkpointPath);
        PrivateDirectory? journal = null, authority = null;
        FileStream? coordinator = null;
        try
        {
            journal = PrivateDirectory.Create(journalName, "journal", step);
            authority = PrivateDirectory.Create(authorityName, "checkpoint-root", step);
            coordinator = journal.OpenFile(LockName, create: true, writable: true);
            if (Flock(coordinator.SafeFileHandle, 2 | 4) != 0) throw Io();
            step?.Invoke("lock:created");
            var storeId = Guid.NewGuid(); var header = Header(storeId);
            coordinator.Write(header); Flush(coordinator); step?.Invoke("lock:file-synced");
            journal.Sync(); step?.Invoke("lock:directory-synced");
            var storage = new LinuxNativeWorkJournalStorage(journal, authority, coordinator, storeId, step);
            var initial = NativeWorkLedger.Encode(new(1, storeId, 0, []));
            var checkpoint = NativeWorkCheckpointProtocol.Encode(NativeWorkCheckpointProtocol.Initialize(storage.Binding, initial));
            storage.ValidateBinding();
            storage.WriteInitial(authority, CheckpointName, checkpoint, "checkpoint");
            storage.WriteInitial(journal, LedgerName, initial, "ledger");
            storage.ValidateBinding();
            Exact(storage.ReadLedger(), initial); Exact(storage.ReadCheckpoint(), checkpoint);
            return storage;
        }
        catch
        {
            if (coordinator is not null) { _ = Flock(coordinator.SafeFileHandle, 8); coordinator.Dispose(); }
            authority?.Dispose(); journal?.Dispose(); throw;
        }
    }

    public static LinuxNativeWorkJournalStorage OpenExisting(string journalPath, string checkpointPath, Action<string>? step = null)
    {
        var (journalName, authorityName) = Paths(journalPath, checkpointPath);
        PrivateDirectory? journal = null, authority = null;
        FileStream? coordinator = null;
        try
        {
            journal = PrivateDirectory.Open(journalName, privateOwner: true);
            authority = PrivateDirectory.Open(authorityName, privateOwner: true);
            coordinator = journal.OpenFile(LockName, create: false, writable: true);
            if (Flock(coordinator.SafeFileHandle, 2 | 4) != 0) throw Io();
            var bytes = Read(coordinator, 128);
            var text = Utf8.GetString(bytes);
            const string prefix = "optimisarr-native-work-lock-v1:";
            if (!text.StartsWith(prefix, StringComparison.Ordinal)
                || !Guid.TryParseExact(text[prefix.Length..].TrimEnd('\n'), "N", out var storeId) || storeId == Guid.Empty)
                throw new InvalidDataException("Native accounting coordinator authority is invalid.");
            Exact(bytes, Header(storeId));
            var storage = new LinuxNativeWorkJournalStorage(journal, authority, coordinator, storeId, step);
            storage.ValidateBinding();
            return storage;
        }
        catch
        {
            if (coordinator is not null) { _ = Flock(coordinator.SafeFileHandle, 8); coordinator.Dispose(); }
            authority?.Dispose(); journal?.Dispose(); throw;
        }
    }

    public void ValidateBinding()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _journal.Validate(); _authority.Validate();
        if (Inspect(_coordinator.SafeFileHandle, directory: false, privateOwner: true) != _lockIdentity) throw Changed();
        _journal.RequireEntry(LockName, _lockIdentity);
        using var file = _journal.OpenFile(LockName);
        Exact(Read(file, 128), _header);
    }

    public byte[] ReadLedger() { ValidateBinding(); return _journal.ReadFile(LedgerName, NativeWorkLedger.MaximumBytes); }
    public byte[] ReadCheckpoint() { ValidateBinding(); return _authority.ReadFile(CheckpointName, NativeWorkCheckpointProtocol.MaximumBytes); }

    public void ReconfirmLedger(ReadOnlySpan<byte> expected)
    {
        ValidateBinding();
        using var file = _journal.OpenFile(LedgerName);
        var identity = Inspect(file.SafeFileHandle, directory: false, privateOwner: true);
        Exact(Read(file, NativeWorkLedger.MaximumBytes), expected);
        Sync(file.SafeFileHandle); _step?.Invoke("ledger:reconfirm-file-synced");
        _journal.Sync(); _step?.Invoke("ledger:reconfirm-directory-synced");
        // Syncing a predecessor that has been unlinked cannot confirm the named generation.
        _journal.RequireEntry(LedgerName, identity);
        Exact(_journal.ReadFile(LedgerName, NativeWorkLedger.MaximumBytes), expected);
        ValidateBinding();
    }

    public void WriteCheckpoint(ReadOnlySpan<byte> bytes)
    {
        ValidateBinding();
        var checkpoint = NativeWorkCheckpointProtocol.Decode(bytes);
        if (checkpoint.Binding != Binding || checkpoint.StoreId != _storeId) throw Changed();
        Replace(_authority, CheckpointName, bytes, "checkpoint");
    }

    public void ReplaceLedger(ReadOnlySpan<byte> bytes)
    {
        ValidateBinding();
        if (NativeWorkLedger.Decode(bytes).StoreId != _storeId) throw Changed();
        Replace(_journal, LedgerName, bytes, "ledger");
    }

    private void WriteInitial(PrivateDirectory directory, string name, ReadOnlySpan<byte> bytes, string phase)
    {
        using var file = directory.OpenFile(name, create: true, writable: true);
        _step?.Invoke(phase + ":created");
        file.Write(bytes); Flush(file); _step?.Invoke(phase + ":file-synced");
        directory.Sync(); _step?.Invoke(phase + ":directory-synced");
        directory.RequireEntry(name, Inspect(file.SafeFileHandle, directory: false, privateOwner: true));
        Exact(directory.ReadFile(name, bytes.Length), bytes);
    }

    private void Replace(PrivateDirectory directory, string name, ReadOnlySpan<byte> bytes, string phase)
    {
        using var original = directory.OpenFile(name);
        var before = Inspect(original.SafeFileHandle, directory: false, privateOwner: true);
        var temporary = name + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using var file = directory.OpenFile(temporary, create: true, writable: true);
        var replacement = Inspect(file.SafeFileHandle, directory: false, privateOwner: true);
        _step?.Invoke(phase + ":temp-created");
        file.Write(bytes); Flush(file); _step?.Invoke(phase + ":file-synced");
        file.Position = 0; Exact(Read(file, bytes.Length), bytes);
        ValidateBinding(); directory.RequireEntry(name, before); directory.RequireEntry(temporary, replacement);
        if (RenameAt(directory.Descriptor, temporary, directory.Descriptor, name) != 0) throw Io();
        _step?.Invoke(phase + ":renamed");
        // File synchronization alone does not persist rename's directory entry.
        // https://man7.org/linux/man-pages/man2/fsync.2.html
        directory.Sync(); _step?.Invoke(phase + ":directory-synced");
        directory.RequireEntry(name, replacement);
        Exact(directory.ReadFile(name, bytes.Length), bytes); ValidateBinding();
    }

    private static (string Journal, string Authority) Paths(string journal, string authority)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
            throw new PlatformNotSupportedException("Native Linux accounting requires a supported 64-bit Linux host.");
        static string Normalize(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Contains('\0') || !System.IO.Path.IsPathFullyQualified(value)) throw new ArgumentException("Native accounting requires an absolute private path.");
            var full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(value));
            if (full == "/" || full != value || Utf8.GetByteCount(full) > 4096) throw new ArgumentException("Native accounting requires a normalized private path.");
            return full;
        }
        journal = Normalize(journal); authority = Normalize(authority);
        if (journal == authority || journal.StartsWith(authority + "/", StringComparison.Ordinal) || authority.StartsWith(journal + "/", StringComparison.Ordinal))
            throw new ArgumentException("Native checkpoint storage must be outside the journal directory.");
        return (journal, authority);
    }

    private static byte[] Header(Guid id) => Utf8.GetBytes("optimisarr-native-work-lock-v1:" + id.ToString("N") + "\n");
    private static byte[] Read(FileStream file, int maximum)
    {
        var length = file.Length;
        if (length is <= 0 || length > maximum) throw new IOException("Native accounting file exceeds its supported bound or is incomplete.");
        file.Position = 0;
        var bytes = new byte[(int)length]; file.ReadExactly(bytes);
        if (file.ReadByte() != -1) throw Changed();
        return bytes;
    }
    private static void Flush(FileStream file) { file.Flush(); Sync(file.SafeFileHandle); }
    private static void Sync(SafeFileHandle handle) { if (Fsync(handle) != 0) throw Io(); }
    private static void Exact(ReadOnlySpan<byte> visible, ReadOnlySpan<byte> expected) { if (!visible.SequenceEqual(expected)) throw Changed(); }
    private static IOException Changed() => new("Native accounting identity or exact readback changed.");
    private static IOException Io() => new("Native Linux accounting I/O was refused.", new Win32Exception(Marshal.GetLastPInvokeError()));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        IOException? error = null;
        try { if (Flock(_coordinator.SafeFileHandle, 8) != 0) error = Io(); }
        finally { _coordinator.Dispose(); _authority.Dispose(); _journal.Dispose(); }
        if (error is not null) throw error;
    }

    private sealed record Identity(uint Major, uint Minor, ulong Inode, ulong Mount)
    { public string Key => $"linux:{Major}:{Minor}:{Inode}"; }

    private static Identity Inspect(SafeFileHandle handle, bool directory, bool privateOwner)
    {
        if (Statx(handle, "", 0x1000, 0x17ff, out var info) != 0) throw Io();
        if ((info.Mask & 0x110f) != 0x110f || info.Inode == 0 || info.Mount == 0 || info.Links == 0
            || (info.Mode & 0xf000) != (directory ? 0x4000 : 0x8000) || !directory && info.Links != 1) throw Changed();
        var user = GetEffectiveUserId();
        if (privateOwner)
        {
            if (info.UserId != user || (info.Mode & 0xfff) != (directory ? 0x1c0 : 0x180))
                throw new IOException("Native accounting requires private owner-only directories and files.");
        }
        else if (info.UserId != 0 && info.UserId != user || (info.Mode & 0x12) != 0 && !(info.UserId == 0 && (info.Mode & 0x200) != 0))
            throw new IOException("Native accounting path ancestors are writable by another user.");
        return new(info.Major, info.Minor, info.Inode, info.Mount);
    }

    private sealed class PrivateDirectory : IDisposable
    {
        private readonly SafeFileHandle _handle;
        public string Path { get; }
        public Identity Identity { get; }
        public int Descriptor => _handle.DangerousGetHandle().ToInt32();
        private PrivateDirectory(string path, SafeFileHandle handle, bool privateOwner)
        {
            _handle = handle; Path = path;
            try { Identity = Inspect(handle, directory: true, privateOwner); }
            catch { handle.Dispose(); throw; }
        }
        public static PrivateDirectory Open(string path, bool privateOwner)
        {
            var current = new PrivateDirectory("/", Handle(OpenAt(-100, "/", CloseOnExec | NoFollow | DirectoryFlag, 0)), privateOwner: false);
            try
            {
                var names = path[1..].Split('/', StringSplitOptions.RemoveEmptyEntries);
                for (var index = 0; index < names.Length; index++)
                {
                    var next = new PrivateDirectory(System.IO.Path.Combine(current.Path, names[index]),
                        Handle(OpenAt(current.Descriptor, names[index], CloseOnExec | NoFollow | DirectoryFlag, 0)),
                        privateOwner && index == names.Length - 1);
                    current.Dispose(); current = next;
                }
                current.RequirePersistent(); return current;
            }
            catch { current.Dispose(); throw; }
        }
        public static PrivateDirectory Create(string path, string phase, Action<string>? step)
        {
            using var parent = Open(System.IO.Path.GetDirectoryName(path)!, privateOwner: false);
            var name = System.IO.Path.GetFileName(path);
            if (MkdirAt(parent.Descriptor, name, 0x1c0) != 0) throw Io();
            step?.Invoke(phase + ":created");
            parent.Sync(); step?.Invoke(phase + ":parent-synced");
            var directory = new PrivateDirectory(path, Handle(OpenAt(parent.Descriptor, name, CloseOnExec | NoFollow | DirectoryFlag, 0)), privateOwner: true);
            try { directory.RequirePersistent(); directory.Validate(); return directory; }
            catch { directory.Dispose(); throw; }
        }
        private void RequirePersistent()
        {
            if (Fstatfs(_handle, out var filesystem) != 0) throw Io();
            if (filesystem.Type is not (0xef53 or 0x58465342 or 0x9123683e or 0x2fc12fc1))
                throw new IOException("Native accounting requires supported persistent local storage (ext4, XFS, Btrfs or ZFS).");
        }
        public void Validate()
        {
            if (Inspect(_handle, directory: true, privateOwner: true) != Identity) throw Changed();
            RequirePersistent();
            using var current = Open(Path, privateOwner: true);
            if (current.Identity != Identity) throw Changed();
        }
        public FileStream OpenFile(string name, bool create = false, bool writable = false)
        {
            var handle = Handle(OpenAt(Descriptor, name, CloseOnExec | NoFollow | NonBlocking
                | (writable ? 2 : 0) | (create ? 0x40 | 0x80 : 0), 0x180));
            try
            {
                var identity = Inspect(handle, directory: false, privateOwner: true);
                if (identity.Major != Identity.Major || identity.Minor != Identity.Minor || identity.Mount != Identity.Mount) throw Changed();
                return new FileStream(handle, writable ? FileAccess.ReadWrite : FileAccess.Read);
            }
            catch { handle.Dispose(); throw; }
        }
        public void RequireEntry(string name, Identity expected)
        {
            using var current = OpenFile(name);
            if (Inspect(current.SafeFileHandle, directory: false, privateOwner: true) != expected) throw Changed();
        }
        public byte[] ReadFile(string name, int maximum)
        {
            using var file = OpenFile(name);
            var identity = Inspect(file.SafeFileHandle, directory: false, privateOwner: true);
            var bytes = Read(file, maximum); RequireEntry(name, identity); return bytes;
        }
        public void Sync() => LinuxNativeWorkJournalStorage.Sync(_handle);
        public void Dispose() => _handle.Dispose();
    }

    private static SafeFileHandle Handle(int descriptor) { if (descriptor < 0) throw Io(); return new(new IntPtr(descriptor), ownsHandle: true); }
    [StructLayout(LayoutKind.Explicit, Size = 256)] private struct LinuxInfo
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(16)] public uint Links;
        [FieldOffset(20)] public uint UserId;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(32)] public ulong Inode;
        [FieldOffset(136)] public uint Major;
        [FieldOffset(140)] public uint Minor;
        [FieldOffset(144)] public ulong Mount;
    }
    [StructLayout(LayoutKind.Explicit, Size = 120)] private struct FileSystemInfo
    { [FieldOffset(0)] public long Type; }
    [DllImport("libc", EntryPoint = "openat", SetLastError = true)] private static extern int OpenAt(int directory, string path, int flags, uint mode);
    [DllImport("libc", EntryPoint = "mkdirat", SetLastError = true)] private static extern int MkdirAt(int directory, string name, uint mode);
    [DllImport("libc", EntryPoint = "renameat", SetLastError = true)] private static extern int RenameAt(int sourceDirectory, string source, int targetDirectory, string target);
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)] private static extern int Statx(SafeFileHandle descriptor, string path, int flags, uint mask, out LinuxInfo info);
    [DllImport("libc", EntryPoint = "fstatfs", SetLastError = true)] private static extern int Fstatfs(SafeFileHandle descriptor, out FileSystemInfo info);
    [DllImport("libc", EntryPoint = "flock", SetLastError = true)] private static extern int Flock(SafeFileHandle descriptor, int operation);
    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)] private static extern int Fsync(SafeFileHandle descriptor);
    [DllImport("libc", EntryPoint = "geteuid")] private static extern uint GetEffectiveUserId();
}
