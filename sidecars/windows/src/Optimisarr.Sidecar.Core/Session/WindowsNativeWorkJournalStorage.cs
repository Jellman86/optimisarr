using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Optimisarr.Sidecar.Core.Session;

/// <summary>
/// Experimental NTFS candidate for durable accounting. It proves private creation-time
/// security, pinned identities, exclusive coordination, exact visible readback and
/// fail-closed recovery in a running OS. It does not establish Linux-equivalent
/// namespace durability through power loss; physical qualification is still required
/// before standalone integration, and nothing here is wired to admission.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsNativeWorkJournalStorage : INativeWorkJournalStorage
{
    private const string LedgerName = "ledger.json", CheckpointName = "checkpoint.json", LockName = "coordinator.lock";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly PrivateDirectory _journal, _authority;
    private readonly FileStream _coordinator;
    private readonly Identity _lockIdentity;
    private readonly Guid _storeId;
    private readonly byte[] _header;
    private readonly Action<string>? _step;
    private int _disposed;
    public NativeWorkBinding Binding { get; }

    private WindowsNativeWorkJournalStorage(PrivateDirectory journal, PrivateDirectory authority, FileStream coordinator,
        Guid storeId, Action<string>? step)
    {
        _journal = journal; _authority = authority; _coordinator = coordinator; _storeId = storeId;
        _lockIdentity = Inspect(coordinator.SafeFileHandle, directory: false, privateOwner: true);
        _header = Header(storeId); _step = step;
        Binding = new(journal.Path, journal.Identity.Key, _lockIdentity.Key + ":" + storeId.ToString("N"));
        if (journal.Identity == authority.Identity
            || journal.ChainIdentities.Contains(authority.Identity)
            || authority.ChainIdentities.Contains(journal.Identity))
            throw new IOException("Native checkpoint storage must be physically independent of the journal directory.");
    }

    public static WindowsNativeWorkJournalStorage ProvisionNew(string journalPath, string checkpointPath, Action<string>? step = null)
    {
        var (journalName, authorityName) = Paths(journalPath, checkpointPath);
        PrivateDirectory? journal = null, authority = null;
        FileStream? coordinator = null;
        try
        {
            journal = PrivateDirectory.Create(journalName, "journal", step);
            authority = PrivateDirectory.Create(authorityName, "checkpoint-root", step);
            coordinator = journal.OpenFile(LockName, create: true, writable: true, coordinatorLock: true);
            step?.Invoke("lock:created");
            var storeId = Guid.NewGuid(); var header = Header(storeId);
            coordinator.Write(header); Flush(coordinator); step?.Invoke("lock:file-synced");
            journal.FlushDirectory(); step?.Invoke("lock:directory-flushed");
            var storage = new WindowsNativeWorkJournalStorage(journal, authority, coordinator, storeId, step);
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
            coordinator?.Dispose(); authority?.Dispose(); journal?.Dispose(); throw;
        }
    }

    public static WindowsNativeWorkJournalStorage OpenExisting(string journalPath, string checkpointPath, Action<string>? step = null)
    {
        var (journalName, authorityName) = Paths(journalPath, checkpointPath);
        PrivateDirectory? journal = null, authority = null;
        FileStream? coordinator = null;
        try
        {
            journal = PrivateDirectory.Open(journalName, privateOwner: true);
            authority = PrivateDirectory.Open(authorityName, privateOwner: true);
            coordinator = journal.OpenFile(LockName, create: false, writable: true, coordinatorLock: true);
            var bytes = Read(coordinator, 128);
            var text = Utf8.GetString(bytes);
            const string prefix = "optimisarr-native-work-lock-v1:";
            if (!text.StartsWith(prefix, StringComparison.Ordinal)
                || !Guid.TryParseExact(text[prefix.Length..].TrimEnd('\n'), "N", out var storeId) || storeId == Guid.Empty)
                throw new InvalidDataException("Native accounting coordinator authority is invalid.");
            Exact(bytes, Header(storeId));
            var storage = new WindowsNativeWorkJournalStorage(journal, authority, coordinator, storeId, step);
            storage.ValidateBinding();
            return storage;
        }
        catch
        {
            coordinator?.Dispose(); authority?.Dispose(); journal?.Dispose(); throw;
        }
    }

    public void ValidateBinding()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _journal.Validate(); _authority.Validate();
        if (Inspect(_coordinator.SafeFileHandle, directory: false, privateOwner: true) != _lockIdentity) throw Changed();
        _journal.RequireEntry(LockName, _lockIdentity);
        // The held no-sharing coordinator cannot be reopened for data; read its
        // header through the retained handle instead of a second named open.
        Exact(Read(_coordinator, 128), _header);
    }

    public byte[] ReadLedger() { ValidateBinding(); return _journal.ReadFile(LedgerName, NativeWorkLedger.MaximumBytes); }
    public byte[] ReadCheckpoint() { ValidateBinding(); return _authority.ReadFile(CheckpointName, NativeWorkCheckpointProtocol.MaximumBytes); }

    public void ReconfirmLedger(ReadOnlySpan<byte> expected)
    {
        ValidateBinding();
        Identity identity;
        using (var file = _journal.OpenFile(LedgerName, create: false, writable: true, writeThrough: true))
        {
            identity = Inspect(file.SafeFileHandle, directory: false, privateOwner: true);
            Exact(Read(file, NativeWorkLedger.MaximumBytes), expected);
            // FlushFileBuffers requires write access, hence the writable open. The named
            // entry cannot be swapped beneath this no-sharing handle; the identity
            // recheck after closing still proves the flushed object is the visible one.
            Flush(file); _step?.Invoke("ledger:reconfirm-file-synced");
        }
        _journal.FlushDirectory(); _step?.Invoke("ledger:reconfirm-directory-flushed");
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
        Identity identity;
        using (var file = directory.OpenFile(name, create: true, writable: true, writeThrough: true))
        {
            identity = Inspect(file.SafeFileHandle, directory: false, privateOwner: true);
            _step?.Invoke(phase + ":created");
            file.Write(bytes); Flush(file); _step?.Invoke(phase + ":file-synced");
        }
        directory.FlushDirectory(); _step?.Invoke(phase + ":directory-flushed");
        directory.RequireEntry(name, identity);
        Exact(directory.ReadFile(name, bytes.Length), bytes);
    }

    private void Replace(PrivateDirectory directory, string name, ReadOnlySpan<byte> bytes, string phase)
    {
        var before = directory.EntryIdentity(name);
        var temporary = name + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Identity replacement;
        using (var file = directory.OpenFile(temporary, create: true, writable: true, writeThrough: true, deleteAccess: true))
        {
            replacement = Inspect(file.SafeFileHandle, directory: false, privateOwner: true);
            _step?.Invoke(phase + ":temp-created");
            file.Write(bytes); Flush(file); _step?.Invoke(phase + ":file-synced");
            file.Position = 0; Exact(Read(file, bytes.Length), bytes);
            ValidateBinding(); directory.RequireEntry(name, before);
            // The identity recheck narrows but cannot eliminate the check-to-rename
            // interval; only cooperative owner processes can reach this private
            // directory, the same residual race as the Linux renameat sequence.
            directory.RenameByHandle(file.SafeFileHandle, name);
            _step?.Invoke(phase + ":renamed");
            // Post-rename flush of the renamed file's own handle. Microsoft documents
            // that write-through requests flush resulting NTFS metadata including a
            // rename, and that FlushFileBuffers flushes file metadata; neither is
            // presented here as a proven directory-synchronization barrier.
            Flush(file); _step?.Invoke(phase + ":rename-flushed");
        }
        directory.FlushDirectory(); _step?.Invoke(phase + ":directory-flushed");
        directory.RequireEntry(name, replacement);
        Exact(directory.ReadFile(name, bytes.Length), bytes); ValidateBinding();
    }

    private static (string Journal, string Authority) Paths(string journal, string authority)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess
            || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
            throw new PlatformNotSupportedException("Native Windows accounting requires a supported 64-bit Windows host.");
        static string Normalize(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Contains('\0') || !Path.IsPathFullyQualified(value)
                || value.StartsWith(@"\\", StringComparison.Ordinal))
                throw new ArgumentException("Native accounting requires an absolute local private path.");
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
            var root = Path.GetPathRoot(full)!;
            if (full == root.TrimEnd('\\') || full != value || Encoding.Unicode.GetByteCount(full) > 8192)
                throw new ArgumentException("Native accounting requires a normalized private path.");
            foreach (var component in full[root.Length..].Split('\\'))
            {
                if (component.Length == 0 || component is "." or ".." || component.EndsWith('.') || component.EndsWith(' ')
                    || component.IndexOfAny(['<', '>', ':', '"', '|', '?', '*', '/']) >= 0
                    || component.Any(char.IsControl))
                    throw new ArgumentException("Native accounting path components must be unambiguous file names.");
            }
            return full;
        }
        journal = Normalize(journal); authority = Normalize(authority);
        if (string.Equals(journal, authority, StringComparison.OrdinalIgnoreCase)
            || journal.StartsWith(authority + "\\", StringComparison.OrdinalIgnoreCase)
            || authority.StartsWith(journal + "\\", StringComparison.OrdinalIgnoreCase))
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
    private static void Flush(FileStream file) { file.Flush(); FlushHandle(file.SafeFileHandle); }
    private static void FlushHandle(SafeFileHandle handle) { if (!FlushFileBuffers(handle)) throw Io(); }
    private static void Exact(ReadOnlySpan<byte> visible, ReadOnlySpan<byte> expected) { if (!visible.SequenceEqual(expected)) throw Changed(); }
    private static IOException Changed() => new("Native accounting identity or exact readback changed.");
    private static IOException Io() => new("Native Windows accounting I/O was refused.", new Win32Exception(Marshal.GetLastPInvokeError()));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // Closing the coordinator handle releases the exclusive open; there is
        // no separate unlock that could fail after ownership has ended.
        _coordinator.Dispose(); _authority.Dispose(); _journal.Dispose();
    }

    private sealed record Identity(string Key, string Mount);

    private static Identity ReadIdentity(SafeFileHandle handle, bool directory)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw Io();
        if ((info.Attributes & 0x400) != 0) throw new IOException("Native accounting reparse points are refused.");
        if ((info.Attributes & 0x10) != 0 != directory || info.Links == 0 || !directory && info.Links != 1) throw Changed();
        var idBuffer = new byte[40];
        if (!GetFileInformationByHandleEx(handle, 18, idBuffer, idBuffer.Length)) throw Io();
        var volume = BitConverter.ToUInt64(idBuffer, 0);
        var fileId = idBuffer.AsSpan(8, 16);
        if (volume == 0 || fileId.IndexOfAnyExcept((byte)0) < 0) throw Changed();
        return new($"windows:{volume:x16}:{Convert.ToHexString(fileId)}", $"windows:{volume:x16}");
    }

    private static Identity Inspect(SafeFileHandle handle, bool directory, bool privateOwner)
    {
        var identity = ReadIdentity(handle, directory);
        ValidateSecurity(handle, privateOwner);
        return identity;
    }

    private static SecurityIdentifier CurrentUser => WindowsIdentity.GetCurrent().User
        ?? throw new IOException("Native accounting cannot identify the current account.");
    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier AdministratorsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    // NT SERVICE\TrustedInstaller owns system ancestors the way root owns them on Linux.
    private static readonly SecurityIdentifier TrustedInstallerSid = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    private static bool IsTrusted(SecurityIdentifier sid, bool ancestor) =>
        sid == CurrentUser || sid == SystemSid || sid == AdministratorsSid || ancestor && sid == TrustedInstallerSid;

    private static void ValidateSecurity(SafeFileHandle handle, bool privateOwner)
    {
        var error = GetSecurityInfo(handle, 1, 0x1 | 0x4, out var ownerSid, out _, out _, out _, out var descriptor);
        if (error != 0 || descriptor == IntPtr.Zero) throw new IOException("Native accounting security is unavailable.", new Win32Exception((int)error));
        try
        {
            var length = GetSecurityDescriptorLength(descriptor);
            var bytes = new byte[length]; Marshal.Copy(descriptor, bytes, 0, (int)length);
            var security = new RawSecurityDescriptor(bytes, 0);
            var owner = security.Owner ?? throw Changed();
            if (ownerSid == IntPtr.Zero || privateOwner && owner != CurrentUser || !privateOwner && !IsTrusted(owner, ancestor: true))
                throw new IOException("Native accounting requires stable trusted ownership.");
            var dacl = security.DiscretionaryAcl;
            if ((security.ControlFlags & ControlFlags.DiscretionaryAclPresent) == 0 || dacl is null)
                throw new IOException("Native accounting requires an explicit private DACL.");
            if (privateOwner && (security.ControlFlags & ControlFlags.DiscretionaryAclProtected) == 0)
                throw new IOException("Native accounting requires a protected private DACL.");
            // POSIX write access on an ancestor directory bundles entry creation
            // with entry deletion, so Linux refuses other-writable ancestors outright.
            // Windows splits those rights. Append/create-directories alone cannot
            // replace pinned, already-open components while leaves are created
            // exclusively with protected DACLs (system-drive roots grant exactly
            // that to all users). But FILE_WRITE_DATA and FILE_WRITE_ATTRIBUTES can
            // set reparse data on an empty directory in place, and the deletion
            // class (FILE_DELETE_CHILD, DELETE, WRITE_DAC, WRITE_OWNER, or generics
            // containing them) can remove or rebind components, so ancestors refuse
            // all of those for untrusted principals.
            const int mutation = 0x2 | 0x40 | 0x100 | 0x10000 | 0x40000 | 0x80000 | 0x10000000 | 0x2000000;
            foreach (GenericAce ace in dacl)
            {
                if (ace is not CommonAce common || common.AceType != AceType.AccessAllowed)
                {
                    // Conditional/callback and object allow ACEs can grant rights
                    // this validator does not model; refuse them on any object.
                    if (privateOwner || ace.AceType is AceType.AccessAllowed or AceType.AccessAllowedObject
                        or AceType.AccessAllowedCallback or AceType.AccessAllowedCallbackObject)
                        throw UnsafeSecurity($"unsupported ACE type {ace.AceType}");
                    continue;
                }
                if (privateOwner)
                {
                    if (common.AceFlags.HasFlag(AceFlags.Inherited) || !IsTrusted(common.SecurityIdentifier, ancestor: false))
                        throw UnsafeSecurity($"ACE for {common.SecurityIdentifier} with flags {common.AceFlags}");
                }
                else if (!common.AceFlags.HasFlag(AceFlags.InheritOnly)
                    && !IsTrusted(common.SecurityIdentifier, ancestor: true) && (common.AccessMask & mutation) != 0)
                    throw UnsafeSecurity($"ancestor ACE for {common.SecurityIdentifier} with mask 0x{common.AccessMask:x}");
            }
            static IOException UnsafeSecurity(string detail) => new($"Native accounting requires private owner-only directories and files ({detail}).");
        }
        finally { LocalFree(descriptor); }
    }

    private static byte[] PrivateDescriptor(bool directory)
    {
        // A null DACL argument here would make .NET substitute an
        // Everyone/full-control ACE; start from an explicitly empty DACL.
        var dacl = new DiscretionaryAcl(directory, false, 1);
        var descriptor = new CommonSecurityDescriptor(directory, false,
            ControlFlags.DiscretionaryAclPresent | ControlFlags.DiscretionaryAclProtected | ControlFlags.SelfRelative,
            CurrentUser, null, null, dacl);
        var inheritance = directory ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None;
        foreach (var sid in new[] { CurrentUser, SystemSid, AdministratorsSid }.Distinct())
            dacl.AddAccess(AccessControlType.Allow, sid, -1, inheritance, PropagationFlags.None);
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        return bytes;
    }

    // Every ancestor handle is retained with delete sharing denied. Unlike the shared
    // workspace walk, this walk never resolves the caller's own path links first:
    // each component is opened as a reparse point and refused if it is one.
    private sealed class PrivateDirectory : IDisposable
    {
        private readonly List<SafeFileHandle> _ancestors = [];
        private readonly SafeFileHandle _leaf;
        private readonly bool _privateOwner;
        public string Path { get; }
        public Identity Identity { get; }
        public IReadOnlyList<Identity> ChainIdentities { get; }
        private string DevicePath => @"\\?\" + Path;

        private PrivateDirectory(string path, List<SafeFileHandle> handles, List<Identity> chain, bool privateOwner)
        {
            Path = path; _privateOwner = privateOwner;
            _leaf = handles[^1]; _ancestors.AddRange(handles);
            Identity = chain[^1]; ChainIdentities = chain;
        }

        public static PrivateDirectory Open(string path, bool privateOwner)
        {
            var root = System.IO.Path.GetPathRoot(path)!;
            var names = path[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
            var handles = new List<SafeFileHandle>();
            var chain = new List<Identity>();
            try
            {
                var current = root;
                handles.Add(OpenDirectoryHandle(@"\\?\" + current)); chain.Add(Inspect(handles[^1], directory: true, privateOwner: false));
                for (var index = 0; index < names.Length; index++)
                {
                    current = System.IO.Path.Combine(current, names[index]);
                    handles.Add(OpenDirectoryHandle(@"\\?\" + current));
                    chain.Add(Inspect(handles[^1], directory: true, privateOwner && index == names.Length - 1));
                    if (chain[^1].Mount != chain[0].Mount) throw Changed();
                }
                var directory = new PrivateDirectory(path, handles, chain, privateOwner);
                try { directory.RequireNtfs(); return directory; }
                catch { directory.Dispose(); throw; }
            }
            catch { foreach (var handle in handles) handle.Dispose(); throw; }
        }

        public static PrivateDirectory Create(string path, string phase, Action<string>? step)
        {
            using var parent = Open(System.IO.Path.GetDirectoryName(path)!, privateOwner: false);
            var descriptor = PrivateDescriptor(directory: true);
            var pin = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
            try
            {
                var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = pin.AddrOfPinnedObject() };
                if (!CreateDirectoryW(@"\\?\" + path, ref attributes)) throw Io();
            }
            finally { pin.Free(); }
            step?.Invoke(phase + ":created");
            parent.FlushDirectory(); step?.Invoke(phase + ":parent-flushed");
            var directory = Open(path, privateOwner: true);
            try { directory.Validate(); return directory; }
            catch { directory.Dispose(); throw; }
        }

        private static SafeFileHandle OpenDirectoryHandle(string devicePath)
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>() };
            var handle = CreateFileW(devicePath, 0x1 | 0x20 | 0x80 | 0x20000, 1 | 2, ref attributes, 3, 0x2000000 | 0x200000, IntPtr.Zero);
            if (handle.IsInvalid) { handle.Dispose(); throw Io(); }
            return handle;
        }

        private void RequireNtfs()
        {
            var name = new StringBuilder(64);
            if (!GetVolumeInformationByHandleW(_leaf, null, 0, out _, out _, out _, name, name.Capacity)
                || !string.Equals(name.ToString(), "NTFS", StringComparison.OrdinalIgnoreCase))
                throw new IOException("Native Windows accounting candidate requires local NTFS storage.");
        }

        public void Validate()
        {
            for (var index = 0; index < _ancestors.Count; index++)
                if (Inspect(_ancestors[index], directory: true, _privateOwner && index == _ancestors.Count - 1) != ChainIdentities[index]) throw Changed();
            RequireNtfs();
            using var current = Open(Path, _privateOwner);
            if (current.Identity != Identity) throw Changed();
        }

        public void FlushDirectory()
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>() };
            var handle = CreateFileW(DevicePath, 0x40000000 | 0x80 | 0x20000, 1 | 2, ref attributes, 3, 0x2000000 | 0x200000, IntPtr.Zero);
            if (handle.IsInvalid) { handle.Dispose(); throw Io(); }
            using (handle)
            {
                if (ReadIdentity(handle, directory: true) != Identity) throw Changed();
                FlushHandle(handle);
            }
        }

        public FileStream OpenFile(string name, bool create, bool writable, bool writeThrough = false,
            bool deleteAccess = false, bool coordinatorLock = false)
        {
            var access = (writable ? 0x80000000u | 0x40000000u : 0x80000000u) | 0x20000u | (deleteAccess ? 0x10000u : 0);
            var share = writable || coordinatorLock ? 0u : 1u;
            var flags = 0x200000u | (writeThrough ? 0x80000000u : 0);
            SafeFileHandle handle;
            if (create)
            {
                var descriptor = PrivateDescriptor(directory: false);
                var pin = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
                try
                {
                    var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = pin.AddrOfPinnedObject() };
                    handle = CreateFileW(DevicePath + "\\" + name, access, share, ref attributes, 1, flags, IntPtr.Zero);
                }
                finally { pin.Free(); }
            }
            else
            {
                var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>() };
                handle = CreateFileW(DevicePath + "\\" + name, access, share, ref attributes, 3, flags, IntPtr.Zero);
            }
            if (handle.IsInvalid) { handle.Dispose(); throw Io(); }
            try
            {
                var identity = Inspect(handle, directory: false, privateOwner: true);
                if (identity.Mount != Identity.Mount) throw Changed();
                return new FileStream(handle, writable ? FileAccess.ReadWrite : FileAccess.Read);
            }
            catch { handle.Dispose(); throw; }
        }

        // Attribute/security-only probe: requests no data or delete access, so it
        // can inspect the named entry while our own no-sharing handle stays open.
        public Identity EntryIdentity(string name)
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>() };
            var handle = CreateFileW(DevicePath + "\\" + name, 0x80 | 0x20000, 1 | 2 | 4, ref attributes, 3, 0x200000, IntPtr.Zero);
            if (handle.IsInvalid) { handle.Dispose(); throw Io(); }
            using (handle)
            {
                var identity = Inspect(handle, directory: false, privateOwner: true);
                if (identity.Mount != Identity.Mount) throw Changed();
                return identity;
            }
        }

        public void RequireEntry(string name, Identity expected)
        {
            if (EntryIdentity(name) != expected) throw Changed();
        }

        public byte[] ReadFile(string name, int maximum)
        {
            using var file = OpenFile(name, create: false, writable: false);
            var identity = Inspect(file.SafeFileHandle, directory: false, privateOwner: true);
            var bytes = Read(file, maximum);
            if (Inspect(file.SafeFileHandle, directory: false, privateOwner: true) != identity) throw Changed();
            return bytes;
        }

        public void RenameByHandle(SafeFileHandle file, string name)
        {
            // The source is renamed by handle, so its identity cannot be swapped
            // by path. The Win32 wrapper does not honour a RootDirectory handle
            // (the handle-relative form is rejected with ERROR_INVALID_PARAMETER),
            // so the destination is named absolutely beneath this directory,
            // whose retained chain denies delete sharing and whose identity is
            // re-verified here and after the rename by the caller.
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>() };
            var root = CreateFileW(DevicePath, 0x20 | 0x80 | 0x20000, 1 | 2 | 4, ref attributes, 3, 0x2000000 | 0x200000, IntPtr.Zero);
            if (root.IsInvalid) { root.Dispose(); throw Io(); }
            using (root)
                if (ReadIdentity(root, directory: true) != Identity) throw Changed();
            var nameBytes = Encoding.Unicode.GetBytes(DevicePath + "\\" + name);
            var buffer = new byte[20 + nameBytes.Length];
            buffer[0] = 1;
            BitConverter.GetBytes((uint)nameBytes.Length).CopyTo(buffer, 16);
            nameBytes.CopyTo(buffer, 20);
            if (!SetFileInformationByHandle(file, 3, buffer, buffer.Length)) throw Io();
        }

        public void Dispose()
        {
            foreach (var handle in _ancestors) handle.Dispose();
            _ancestors.Clear();
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 52)] private struct WindowsInfo
    {
        [FieldOffset(0)] public uint Attributes;
        [FieldOffset(40)] public uint Links;
    }
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes
    { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, ref SecurityAttributes security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateDirectoryW(string path, ref SecurityAttributes security);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out WindowsInfo info);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind, byte[] buffer, int size);
    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int kind, byte[] buffer, int size);
    [DllImport("kernel32.dll", EntryPoint = "FlushFileBuffers", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FlushFileBuffers(SafeFileHandle handle);
    [DllImport("kernel32.dll", EntryPoint = "GetVolumeInformationByHandleW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetVolumeInformationByHandleW(SafeFileHandle handle, StringBuilder? volume, int volumeSize, out uint serial, out uint componentLength, out uint flags, StringBuilder fileSystem, int fileSystemSize);
    [DllImport("advapi32.dll", EntryPoint = "GetSecurityInfo", SetLastError = true)] private static extern uint GetSecurityInfo(SafeFileHandle handle, int objectType, int securityInformation, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll", EntryPoint = "GetSecurityDescriptorLength")] private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
    [DllImport("kernel32.dll", EntryPoint = "LocalFree")] private static extern IntPtr LocalFree(IntPtr memory);
}
