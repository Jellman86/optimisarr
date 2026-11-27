using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
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
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsFileApi
{
    public const uint ListDirectory = 0x1;
    public const uint ReadAttributes = 0x80;
    public const uint Delete = 0x10000;
    public const uint ReadControl = 0x20000;
    public const uint WriteDac = 0x40000;
    public const uint GenericRead = 0x80000000;

    public const uint ShareRead = 0x1;
    public const uint ShareReadWrite = 0x1 | 0x2;

    public const int ErrorFileNotFound = 2;
    public const int ErrorPathNotFound = 3;
    public const int ErrorAlreadyExists = 183;
    private const int ErrorInvalidOwner = 1307;

    private const uint OpenExisting = 3;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint BackupSemantics = 0x02000000;
    private const uint PersistentAcls = 0x8;
    private const uint OwnerInformation = 0x1;
    private const uint DaclInformation = 0x4;
    private const uint FullControl = 0x1F01FF;

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

    /// <summary>
    /// Creates a folder already locked to SYSTEM and Administrators, owned by Administrators where
    /// this account may give it to them (the service and an elevated prompt both may), and by the
    /// creator otherwise. Zero, or the Windows error.
    /// </summary>
    public static int CreateAdministratorOnlyDirectory(string path)
    {
        var error = CreateDirectory(path, AdministratorOnlyDescriptor(ownedByAdministrators: true));
        return error == ErrorInvalidOwner ? CreateDirectory(path, AdministratorOnlyDescriptor()) : error;
    }

    /// <summary>Zero, or the Windows error. Without a descriptor the folder inherits from its parent.</summary>
    public static int CreateDirectory(string path, byte[]? descriptor)
    {
        if (descriptor is null)
        {
            return CreateDirectoryInherited(path, IntPtr.Zero) ? 0 : Marshal.GetLastPInvokeError();
        }

        var pinned = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = pinned.AddrOfPinnedObject() };
            return CreateDirectorySecured(path, ref attributes) ? 0 : Marshal.GetLastPInvokeError();
        }
        finally { pinned.Free(); }
    }

    /// <summary>
    /// Deletes the object the handle opened: a link as a link, since it was opened as one. POSIX
    /// semantics remove the name at once; FAT and older Windows fall back to removal on close.
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
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr Descriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool Inherit;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryInherited(string path, IntPtr security);

    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectorySecured(string path, ref SecurityAttributes security);

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
