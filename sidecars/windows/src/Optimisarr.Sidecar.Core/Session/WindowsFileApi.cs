using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Optimisarr.Sidecar.Core.Session;

/// <summary>What one opened handle says about the file or folder behind it.</summary>
internal readonly record struct WindowsFileIdentity(uint Attributes, uint Volume, ulong Index, uint Links, ulong Size)
{
    public bool IsLink => (Attributes & 0x400) != 0;
    public bool IsDirectory => (Attributes & 0x10) != 0;
    public bool SameObject(WindowsFileIdentity other) => Volume == other.Volume && Index == other.Index;
}

/// <summary>
/// The Windows calls that judge a path on a handle rather than by name.
///
/// <para>Every open passes <c>FILE_FLAG_OPEN_REPARSE_POINT</c>, so a junction or symbolic link is
/// opened as itself and can be refused, never silently followed. Every open also leaves out
/// <c>FILE_SHARE_DELETE</c> and asks for at least one right that takes part in sharing, so while the
/// handle is held nobody can rename or delete the object to put a link in its place.</para>
///
/// <para>Holding a folder does not stop it changing in place, though: anyone with a handle to an
/// empty folder that may write its attributes can turn it into a junction, and later lookups by
/// name then go wherever it points. So a walk below a held folder lists it through its handle and
/// opens each entry relative to that handle (<see cref="OpenChild"/>), never by a path that would
/// be resolved again from the top.</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsFileApi
{
    public const uint ListDirectory = 0x1;
    public const uint ReadData = 0x1;
    public const uint ReadAttributes = 0x80;
    public const uint Delete = 0x10000;
    public const uint ReadControl = 0x20000;
    public const uint WriteDac = 0x40000;
    public const uint GenericRead = 0x80000000;

    public const uint ShareRead = 0x1;
    public const uint ShareReadWrite = 0x1 | 0x2;
    public const uint ShareAll = 0x1 | 0x2 | 0x4;

    public const int ErrorFileNotFound = 2;
    public const int ErrorPathNotFound = 3;
    public const int ErrorSharingViolation = 32;
    public const int ErrorAlreadyExists = 183;
    private const int ErrorNoMoreFiles = 18;
    private const int ErrorInvalidOwner = 1307;

    private const uint OpenExisting = 3;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint BackupSemantics = 0x02000000;
    private const uint PersistentAcls = 0x8;
    private const uint OwnerInformation = 0x1;
    private const uint DaclInformation = 0x4;
    private const uint FullControl = 0x1F01FF;
    private const uint Synchronize = 0x100000;

    private const uint FileOpen = 1;
    private const uint FileCreate = 2;
    private const uint FileOpenIf = 3;
    private const uint DirectoryFileOption = 0x1;
    private const uint SynchronousIoOption = 0x20;
    private const uint NonDirectoryFileOption = 0x40;
    private const uint BackupIntentOption = 0x4000;
    private const uint OpenReparsePointOption = 0x200000;
    private const uint CaseInsensitive = 0x40;

    private static readonly Lazy<string?> Self = new(() =>
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value;
    });

    /// <summary>The account this process runs as: SYSTEM for the service.</summary>
    public static string? CurrentUser => Self.Value;

    public static bool IsMissing(int error) => error is ErrorFileNotFound or ErrorPathNotFound;

    /// <summary>The handle, or null with the Windows error that refused it.</summary>
    public static SafeFileHandle? TryOpen(string path, uint access, uint share, out int error)
    {
        var handle = CreateFile(path, access, share, IntPtr.Zero, OpenExisting, OpenReparsePoint | BackupSemantics, IntPtr.Zero);
        if (!handle.IsInvalid)
        {
            error = 0;
            return handle;
        }

        error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        return null;
    }

    /// <summary>
    /// The named entry directly inside the folder <paramref name="parent"/> holds, opened as itself
    /// (a link is not followed) and looked up in that folder only: no path is resolved, so nothing
    /// above it, and no change to the folder itself, can send the open anywhere else.
    /// </summary>
    public static SafeFileHandle? OpenChild(SafeFileHandle parent, string name, uint access, uint share, out int error) =>
        OpenRelative(parent, name, access, share, FileOpen, 0, null, out error);

    /// <summary>
    /// Creates a folder directly inside <paramref name="parent"/>, failing if anything is already
    /// there, and returns it opened. Without a descriptor it inherits from the parent.
    /// </summary>
    public static SafeFileHandle? CreateChildDirectory(SafeFileHandle parent, string name, byte[]? descriptor, uint access, uint share, out int error) =>
        OpenRelative(parent, name, access, share, FileCreate, DirectoryFileOption, descriptor, out error);

    /// <summary>
    /// A folder created inside <paramref name="parent"/> already locked to SYSTEM and Administrators,
    /// owned by Administrators where this account may give it to them (the service and an elevated
    /// prompt both may), and by the creator otherwise.
    /// </summary>
    public static SafeFileHandle? CreateAdministratorOnlyChildDirectory(SafeFileHandle parent, string name, uint access, uint share, out int error)
    {
        var created = CreateChildDirectory(parent, name, AdministratorOnlyDescriptor(ownedByAdministrators: true), access, share, out error);
        return created is null && error == ErrorInvalidOwner
            ? CreateChildDirectory(parent, name, AdministratorOnlyDescriptor(), access, share, out error)
            : created;
    }

    /// <summary>The named file directly inside <paramref name="parent"/>, created if it is missing; never a folder.</summary>
    public static SafeFileHandle? OpenOrCreateChildFile(SafeFileHandle parent, string name, uint access, uint share, out int error) =>
        OpenRelative(parent, name, access, share, FileOpenIf, NonDirectoryFileOption, null, out error);

    /// <summary>
    /// The names directly inside the folder the handle holds, read through the handle, so they are
    /// that folder's own entries even if its path now leads somewhere else. The handle needs list access.
    /// </summary>
    public static IReadOnlyList<string> ListNames(SafeFileHandle directory)
    {
        const int FullDirectoryInfo = 14, FullDirectoryRestartInfo = 15, NameLengthOffset = 60, NameOffset = 68;
        var names = new List<string>();
        var buffer = new byte[64 * 1024];
        for (var kind = FullDirectoryRestartInfo; ; kind = FullDirectoryInfo)
        {
            if (!GetDirectoryInformation(directory, kind, buffer, (uint)buffer.Length))
            {
                if (Marshal.GetLastPInvokeError() is ErrorNoMoreFiles or ErrorFileNotFound) return names;
                throw Failure("list a folder");
            }

            for (var offset = 0; ;)
            {
                if (offset + NameOffset > buffer.Length) throw new IOException("Windows returned a folder listing that could not be read.");
                var next = BitConverter.ToInt32(buffer, offset);
                var length = BitConverter.ToInt32(buffer, offset + NameLengthOffset);
                if (length <= 0 || length % 2 != 0 || offset + NameOffset + length > buffer.Length || next < 0)
                    throw new IOException("Windows returned a folder listing that could not be read.");
                var name = Encoding.Unicode.GetString(buffer, offset + NameOffset, length);
                if (name is not ("." or "..")) names.Add(name);
                if (next == 0) break;
                offset += next;
            }
        }
    }

    /// <summary>
    /// Why something already on disk must not be adopted as it stands, read through its handle:
    /// see <see cref="WorkAccess.AdoptionProblem"/>. Null on a drive without access control, where
    /// there is nothing to judge and the administrator has accepted that.
    /// </summary>
    public static string? TrustProblem(SafeFileHandle handle, string path)
    {
        if (!HasAccessControl(handle)) return null;
        var (owner, access) = ReadAccess(handle);
        return WorkAccess.AdoptionProblem(path, owner, access, CurrentUser);
    }

    public static WindowsFileIdentity Identify(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw Failure("read what is behind a handle");
        return new(info.Attributes, info.Volume, ((ulong)info.IndexHigh << 32) | info.IndexLow, info.Links,
            ((ulong)info.SizeHigh << 32) | info.SizeLow);
    }

    /// <summary>False on FAT32, exFAT and the like, where any account can do anything to any file.</summary>
    public static bool HasAccessControl(SafeFileHandle handle)
    {
        if (!GetVolumeInformationByHandle(handle, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero, out var flags, IntPtr.Zero, 0))
            throw Failure("read the drive's file system");
        return (flags & PersistentAcls) != 0;
    }

    /// <summary>
    /// The owner and access list, read through the handle so they describe exactly the object it
    /// opened. A null access list is Windows' "no list": full access for everyone. An entry this
    /// code cannot read is counted as one granting everything, so it can only refuse more.
    /// </summary>
    public static (string? Owner, IReadOnlyList<AccessEntry>? Access) ReadAccess(SafeFileHandle handle)
    {
        GetKernelObjectSecurity(handle, OwnerInformation | DaclInformation, null, 0, out var needed);
        if (needed == 0) throw Failure("read the access list");
        var buffer = new byte[needed];
        if (!GetKernelObjectSecurity(handle, OwnerInformation | DaclInformation, buffer, needed, out _)) throw Failure("read the access list");

        var descriptor = new RawSecurityDescriptor(buffer, 0);
        var owner = descriptor.Owner?.Value;
        if ((descriptor.ControlFlags & ControlFlags.DiscretionaryAclPresent) == 0 || descriptor.DiscretionaryAcl is null)
        {
            return (owner, null);
        }

        var entries = new List<AccessEntry>();
        foreach (GenericAce ace in descriptor.DiscretionaryAcl)
        {
            switch (ace)
            {
                case QualifiedAce { AceQualifier: AceQualifier.AccessAllowed } allowed:
                    entries.Add(new(allowed.SecurityIdentifier.Value, unchecked((uint)allowed.AccessMask)));
                    break;
                case QualifiedAce { AceQualifier: AceQualifier.AccessDenied } denied:
                    entries.Add(new(denied.SecurityIdentifier.Value, unchecked((uint)denied.AccessMask), Allows: false));
                    break;
                case QualifiedAce:
                    // Audit and alarm entries grant nothing.
                    break;
                default:
                    entries.Add(new(WorkAccess.Everyone, FullControl));
                    break;
            }
        }

        return (owner, entries);
    }

    /// <summary>
    /// SYSTEM and Administrators full control, inherited by everything beneath, and nothing taken
    /// from the parent. Owned by Administrators when <paramref name="ownedByAdministrators"/>, so the
    /// owner does not depend on the machine's default-owner policy; otherwise by the creator.
    /// </summary>
    public static byte[] AdministratorOnlyDescriptor(bool ownedByAdministrators = false)
    {
        var access = new RawAcl(GenericAcl.AclRevision, 2);
        access.InsertAce(0, Inherited(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)));
        access.InsertAce(1, Inherited(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)));
        var descriptor = new RawSecurityDescriptor(
            ControlFlags.DiscretionaryAclPresent | ControlFlags.DiscretionaryAclProtected | ControlFlags.SelfRelative,
            owner: ownedByAdministrators ? new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) : null,
            group: null, systemAcl: null, discretionaryAcl: access);
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        return bytes;

        static CommonAce Inherited(SecurityIdentifier principal) => new(
            AceFlags.ObjectInherit | AceFlags.ContainerInherit, AceQualifier.AccessAllowed, unchecked((int)FullControl), principal, false, null);
    }

    /// <summary>
    /// Replaces the access list on the object the handle holds, and stops inheriting from its parent.
    /// Set on the handle itself, which changes only this object: the path-based calls also walk
    /// everything beneath, and that walk could be steered through a link already planted inside.
    /// The descriptor's own "protected" flag is what stops inheritance at this level.
    /// </summary>
    public static void LockDown(SafeFileHandle handle)
    {
        if (!SetKernelObjectSecurity(handle, DaclInformation, AdministratorOnlyDescriptor()))
            throw Failure("reset the access list");
    }

    /// <summary>Zero, or the Windows error. The folder inherits its access from its parent.</summary>
    public static int CreateDirectory(string path) => CreateDirectoryInherited(path, IntPtr.Zero) ? 0 : Marshal.GetLastPInvokeError();

    /// <summary>
    /// Deletes the object the handle opened: a link as a link, since it was opened as one. The name
    /// goes when this handle closes (POSIX semantics, even if others still hold it open; FAT and
    /// older Windows wait for the last handle), so a folder's children must be closed before the
    /// folder itself is deleted.
    /// </summary>
    public static void DeleteThroughHandle(SafeFileHandle handle)
    {
        const uint DeleteFlag = 0x1, PosixSemantics = 0x2, IgnoreReadOnly = 0x10;
        var flags = DeleteFlag | PosixSemantics | IgnoreReadOnly;
        if (SetDispositionEx(handle, 21, ref flags, sizeof(uint))) return;

        byte delete = 1;
        if (!SetDisposition(handle, 4, ref delete, 1)) throw Failure("delete");
    }

    public static string Describe(int error) => new Win32Exception(error).Message;

    private static SafeFileHandle? OpenRelative(SafeFileHandle parent, string name, uint access, uint share, uint disposition, uint options,
        byte[]? descriptor, out int error)
    {
        if (name.Length is 0 or > 255 || name is "." or ".." || name.IndexOfAny(['\\', '/', ':', '\0']) >= 0)
            throw new IOException($"\"{name}\" is not the name of something inside a folder.");

        var text = Marshal.StringToHGlobalUni(name);
        var objectName = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        var pinned = descriptor is null ? default : GCHandle.Alloc(descriptor, GCHandleType.Pinned);
        var referenced = false;
        try
        {
            parent.DangerousAddRef(ref referenced);
            var bytes = (ushort)(name.Length * 2);
            Marshal.StructureToPtr(new UnicodeString { Length = bytes, MaximumLength = bytes, Buffer = text }, objectName, false);
            var attributes = new ObjectAttributes
            {
                Length = Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory = parent.DangerousGetHandle(),
                ObjectName = objectName,
                Attributes = CaseInsensitive,
                SecurityDescriptor = descriptor is null ? IntPtr.Zero : pinned.AddrOfPinnedObject(),
            };
            var status = NtCreateFile(out var handle, access | Synchronize, ref attributes, out _, IntPtr.Zero, 0, share, disposition,
                options | SynchronousIoOption | BackupIntentOption | OpenReparsePointOption, IntPtr.Zero, 0);
            if (status >= 0 && !handle.IsInvalid)
            {
                error = 0;
                return handle;
            }

            handle.Dispose();
            error = RtlNtStatusToDosError(status);
            return null;
        }
        finally
        {
            if (referenced) parent.DangerousRelease();
            if (pinned.IsAllocated) pinned.Free();
            Marshal.FreeHGlobal(objectName);
            Marshal.FreeHGlobal(text);
        }
    }

    private static IOException Failure(string action)
    {
        var error = Marshal.GetLastPInvokeError();
        return new IOException($"Windows refused to {action} ({Describe(error)}).", new Win32Exception(error));
    }

    [StructLayout(LayoutKind.Explicit, Size = 52)]
    private struct ByHandleFileInformation
    {
        [FieldOffset(0)] public uint Attributes;
        [FieldOffset(28)] public uint Volume;
        [FieldOffset(32)] public uint SizeHigh;
        [FieldOffset(36)] public uint SizeLow;
        [FieldOffset(40)] public uint Links;
        [FieldOffset(44)] public uint IndexHigh;
        [FieldOffset(48)] public uint IndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public IntPtr Status;
        public IntPtr Information;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(out SafeFileHandle handle, uint access, ref ObjectAttributes attributes, out IoStatusBlock status,
        IntPtr allocationSize, uint fileAttributes, uint share, uint disposition, uint options, IntPtr extendedAttributes, uint extendedAttributesLength);

    [DllImport("ntdll.dll")]
    private static extern int RtlNtStatusToDosError(int status);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDirectoryInformation(SafeFileHandle handle, int informationClass, byte[] buffer, uint size);

    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryInherited(string path, IntPtr security);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation information);

    [DllImport("kernel32.dll", EntryPoint = "GetVolumeInformationByHandleW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationByHandle(SafeFileHandle handle, IntPtr name, uint nameSize, IntPtr serial,
        IntPtr maximumComponent, out uint flags, IntPtr fileSystemName, uint fileSystemNameSize);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKernelObjectSecurity(SafeFileHandle handle, uint requested, byte[]? descriptor, uint length, out uint needed);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetKernelObjectSecurity(SafeFileHandle handle, uint information, byte[] descriptor);

    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDispositionEx(SafeFileHandle handle, int informationClass, ref uint flags, uint size);

    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDisposition(SafeFileHandle handle, int informationClass, ref byte delete, uint size);
}
