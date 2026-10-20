using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Optimisarr.Core.Workers;

namespace Optimisarr.Sidecar.Core.Session;

internal sealed record NativeWorkspaceIdentity(string Key, string Mount, bool Directory, uint Links);
internal sealed class NativeWorkspaceIsDirectoryException : IOException;

/// <summary>Cleanup stays on opened directories, never through a redirected descendant path.</summary>
internal sealed class NativeWorkspaceDirectory : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly List<SafeFileHandle> _ancestors;
    public string FullPath { get; }
    public NativeWorkspaceIdentity Identity { get; }
    private int Descriptor => _handle.DangerousGetHandle().ToInt32();
    private static bool Windows => OperatingSystem.IsWindows();
    private static bool Darwin => OperatingSystem.IsMacOS();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static int DirectoryFlags => Darwin ? 0x100000 | 0x100 | 0x1000000
        : LinuxDirectoryOpenFlags(RuntimeInformation.ProcessArchitecture);

    internal static int LinuxDirectoryOpenFlags(Architecture architecture)
    {
        var flags = LinuxNativeOpenFlags.For(architecture);
        return flags.Directory | flags.NoFollow | 0x80000;
    }

    internal static int LinuxFileOpenFlags(Architecture architecture, FileMode mode, bool locked, bool inherit)
    {
        var flags = (mode == FileMode.Open && !locked ? 0 : 2) | LinuxNativeOpenFlags.For(architecture).NoFollow
            | (inherit ? 0 : 0x80000) | 0x800;
        if (mode is FileMode.CreateNew or FileMode.OpenOrCreate) flags |= 0x40;
        if (mode == FileMode.CreateNew) flags |= 0x80;
        return flags;
    }

    private NativeWorkspaceDirectory(string path, SafeFileHandle handle, List<SafeFileHandle>? ancestors = null)
    {
        _handle = handle; _ancestors = ancestors ?? []; FullPath = path;
        try
        {
            Identity = ReadIdentity(handle);
            if (!Identity.Directory) throw new IOException("The native workspace is not an ordinary directory.");
        }
        catch { Dispose(); throw; }
    }

    public static NativeWorkspaceDirectory OpenRoot(string requested, bool create)
    {
        var full = NativeWorkingVolume.PhysicalRoot(requested);
        var prefix = Path.GetPathRoot(full)!;
        var names = full[prefix.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (Windows)
        {
            var handles = new List<SafeFileHandle>();
            try
            {
                var path = prefix;
                handles.Add(OpenWindowsDirectory(path, delete: false));
                foreach (var name in names)
                {
                    path = Path.Combine(path, name);
                    if (create && !Directory.Exists(path) && !CreateDirectoryWindows(path, IntPtr.Zero)) ThrowIo();
                    handles.Add(OpenWindowsDirectory(path, delete: false));
                }
                var root = handles[^1]; handles.RemoveAt(handles.Count - 1);
                return new(full, root, handles);
            }
            catch { foreach (var handle in handles) handle.Dispose(); throw; }
        }
        if (!OperatingSystem.IsLinux() && !Darwin) throw new PlatformNotSupportedException("Native workspace ownership is unavailable.");
        var directory = new NativeWorkspaceDirectory(prefix, UnixHandle(Open(prefix, DirectoryFlags, 0)));
        try
        {
            foreach (var name in names)
            {
                var fd = OpenAt(directory.Descriptor, name, DirectoryFlags, 0);
                if (fd < 0 && Marshal.GetLastPInvokeError() == 2 && create)
                {
                    if (MkdirAt(directory.Descriptor, name, 0x1c0) != 0 && Marshal.GetLastPInvokeError() != 17) ThrowIo();
                    fd = OpenAt(directory.Descriptor, name, DirectoryFlags, 0);
                }
                var next = new NativeWorkspaceDirectory(Path.Combine(directory.FullPath, name), UnixHandle(fd));
                directory.Dispose(); directory = next;
            }
            return directory;
        }
        catch { directory.Dispose(); throw; }
    }

    public NativeWorkspaceDirectory OpenDirectory(string name, bool create = false)
    {
        CheckName(name);
        if (create)
        {
            if (Windows) { if (!CreateDirectoryWindows(Path.Combine(FullPath, name), IntPtr.Zero)) ThrowIo(); }
            else if (MkdirAt(Descriptor, name, 0x1c0) != 0) ThrowIo();
        }
        var handle = Windows ? OpenWindowsDirectory(Path.Combine(FullPath, name), delete: true)
            : UnixHandle(OpenAt(Descriptor, name, DirectoryFlags, 0));
        var child = new NativeWorkspaceDirectory(Path.Combine(FullPath, name), handle);
        if (child.Identity.Mount != Identity.Mount) { child.Dispose(); throw new IOException("A native workspace descendant changed mount."); }
        return child;
    }

    public FileStream OpenFile(string name, FileMode mode = FileMode.Open, bool locked = false, bool inherit = false)
    {
        CheckName(name);
        SafeFileHandle handle;
        if (Windows)
        {
            var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Inherit = inherit };
            handle = CreateFileWindows(Path.Combine(FullPath, name), mode == FileMode.Open && !locked ? 0x10080u | 0x80000000u : 0xc0000000u,
                locked ? 0u : 3u, ref security, mode == FileMode.CreateNew ? 1u : mode == FileMode.OpenOrCreate ? 4u : 3u,
                0x200000, IntPtr.Zero);
            if (handle.IsInvalid) { handle.Dispose(); ThrowIo(); }
        }
        else
        {
            var flags = Darwin ? (mode == FileMode.Open && !locked ? 0 : 2) | 0x100
                | (inherit ? 0 : 0x1000000) | 4
                : LinuxFileOpenFlags(RuntimeInformation.ProcessArchitecture, mode, locked, inherit);
            if (Darwin && (mode is FileMode.CreateNew or FileMode.OpenOrCreate)) flags |= 0x200;
            if (Darwin && mode == FileMode.CreateNew) flags |= 0x800;
            handle = UnixHandle(OpenAt(Descriptor, name, flags, 0x180));
        }
        try
        {
            var identity = ReadIdentity(handle);
            if (identity.Directory) throw new NativeWorkspaceIsDirectoryException();
            if (identity.Links != 1 || identity.Mount != Identity.Mount)
                throw new IOException("The native workspace file is redirected, shared or on another mount.");
            if (locked && !Windows && Flock(handle, 2 | 4) != 0) ThrowIo();
            return new FileStream(handle, mode == FileMode.Open && !locked ? FileAccess.Read : FileAccess.ReadWrite);
        }
        catch { handle.Dispose(); throw; }
    }

    public IEnumerable<string> Names()
    {
        if (Windows)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(FullPath)) yield return Path.GetFileName(entry);
            yield break;
        }
        var fd = OpenAt(Descriptor, ".", DirectoryFlags, 0);
        if (fd < 0) ThrowIo();
        var stream = FdOpenDir(fd);
        if (stream == IntPtr.Zero) { Close(fd); ThrowIo(); }
        try
        {
            while (true)
            {
                var entry = ReadDir(stream);
                if (entry == IntPtr.Zero)
                {
                    if (Marshal.GetLastPInvokeError() != 0) ThrowIo();
                    yield break;
                }
                var recordBytes = (ushort)Marshal.ReadInt16(entry, 16);
                var start = Darwin ? 21 : 19;
                var length = Darwin ? (ushort)Marshal.ReadInt16(entry, 18) : 0;
                if (recordBytes <= start || recordBytes > 4096) throw new IOException("Invalid native directory entry.");
                if (!Darwin) while (start + length < recordBytes && Marshal.ReadByte(entry, start + length) != 0) length++;
                if (length > 1024 || start + length >= recordBytes) throw new IOException("Invalid native directory name.");
                var bytes = new byte[length]; Marshal.Copy(IntPtr.Add(entry, start), bytes, 0, length);
                var name = StrictUtf8.GetString(bytes);
                if (name is "." or "..") continue;
                CheckName(name); yield return name;
            }
        }
        finally { CloseDir(stream); }
    }

    public void DeleteFile(string name, NativeWorkspaceIdentity expected, SafeFileHandle? opened = null)
    {
        CheckName(name);
        if (opened is null)
        {
            using var file = OpenFile(name);
            DeleteFile(name, expected, file.SafeFileHandle); return;
        }
        if (ReadIdentity(opened) != expected) throw new IOException("Native workspace file ownership changed.");
        if (Windows) DeleteWindows(opened);
        else
        {
            using var current = OpenFile(name);
            if (ReadIdentity(current.SafeFileHandle) != expected) throw new IOException("Native workspace file was replaced.");
            if (UnlinkAt(Descriptor, name, 0) != 0) ThrowIo();
        }
    }

    public void DeleteDirectory(string name, NativeWorkspaceDirectory child)
    {
        CheckName(name);
        if (Windows) DeleteWindows(child._handle);
        else
        {
            using var current = OpenDirectory(name);
            if (current.Identity.Key != child.Identity.Key || current.Identity.Mount != child.Identity.Mount)
                throw new IOException("Native workspace directory was replaced.");
            if (UnlinkAt(Descriptor, name, Darwin ? 0x80 : 0x200) != 0) ThrowIo();
        }
    }

    public static NativeWorkspaceIdentity ReadIdentity(SafeFileHandle handle)
    {
        if (Windows)
        {
            if (!GetFileInformation(handle, out var info)) ThrowIo();
            if ((info.Attributes & 0x400) != 0) throw new IOException("Native workspace reparse points are refused.");
            if (info.Volume == 0 || (info.IndexHigh | info.IndexLow) == 0 || info.Links == 0) throw new IOException("Native workspace file identity is unavailable.");
            return new($"windows:{info.Volume}:{info.IndexHigh}:{info.IndexLow}", $"windows:{info.Volume}", (info.Attributes & 16) != 0, info.Links);
        }
        if (Darwin)
        {
            if (Fstat(handle, out var info) != 0) ThrowIo();
            if (info.Inode == 0 || info.Links == 0 || (info.Mode & 0xf000) is not (0x4000 or 0x8000)) throw new IOException("Native workspace special files are refused.");
            return new($"darwin:{info.Device}:{info.Inode}", $"darwin:{info.Device}", (info.Mode & 0xf000) == 0x4000, info.Links);
        }
        if (Statx(handle.DangerousGetHandle().ToInt32(), "", 0x1000, 0x17ff, out var linux) != 0
            || (linux.Mask & 0x1107) != 0x1107 || linux.Inode == 0 || linux.Links == 0 || linux.Mount == 0
            || (linux.Mode & 0xf000) is not (0x4000 or 0x8000))
            throw new IOException("Native workspace filesystem identity is unavailable.");
        return new($"linux:{linux.Major}:{linux.Minor}:{linux.Inode}", $"linux:{linux.Major}:{linux.Minor}:{linux.Mount}", (linux.Mode & 0xf000) == 0x4000, linux.Links);
    }

    private static SafeFileHandle OpenWindowsDirectory(string path, bool delete)
    {
        var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>() };
        var handle = CreateFileWindows(path, 0x81u | (delete ? 0x10000u : 0), 3, ref security, 3, 0x2200000, IntPtr.Zero);
        try
        {
            if (handle.IsInvalid) ThrowIo();
            if (!ReadIdentity(handle).Directory) throw new IOException("Native workspace directory ownership is unavailable.");
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    private static void DeleteWindows(SafeFileHandle handle)
    {
        byte delete = 1;
        if (!SetFileInformation(handle, 4, ref delete, 1)) ThrowIo();
    }
    private static SafeFileHandle UnixHandle(int fd) { if (fd < 0) ThrowIo(); return new(new IntPtr(fd), true); }
    private static void CheckName(string name)
    {
        if (string.IsNullOrEmpty(name) || name is "." or ".." || name.Contains('/') || name.Contains('\0') || Windows && name.Contains('\\'))
            throw new IOException("Invalid native workspace leaf name.");
    }
    private static void ThrowIo() => throw new IOException("Native workspace ownership I/O was refused.", new Win32Exception(Marshal.GetLastPInvokeError()));
    public void Dispose() { _handle.Dispose(); foreach (var handle in _ancestors) handle.Dispose(); }

    [StructLayout(LayoutKind.Explicit, Size = 256)] private struct LinuxInfo
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(16)] public uint Links;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(32)] public ulong Inode;
        [FieldOffset(136)] public uint Major;
        [FieldOffset(140)] public uint Minor;
        [FieldOffset(144)] public ulong Mount;
    }
    [StructLayout(LayoutKind.Explicit, Size = 144)] private struct DarwinInfo
    {
        [FieldOffset(0)] public int Device;
        [FieldOffset(4)] public ushort Mode;
        [FieldOffset(6)] public ushort Links;
        [FieldOffset(8)] public ulong Inode;
    }
    [StructLayout(LayoutKind.Explicit, Size = 52)] private struct WindowsInfo
    {
        [FieldOffset(0)] public uint Attributes;
        [FieldOffset(28)] public uint Volume;
        [FieldOffset(40)] public uint Links;
        [FieldOffset(44)] public uint IndexHigh;
        [FieldOffset(48)] public uint IndexLow;
    }
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes
    { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
    [DllImport("libc", EntryPoint = "open", SetLastError = true)] private static extern int Open(string path, int flags, uint mode);
    private static int OpenAt(int dir, string name, int flags, uint mode) => Darwin && RuntimeInformation.ProcessArchitecture == Architecture.Arm64
        ? OpenAtDarwinArm64(dir, name, flags, 0, 0, 0, 0, 0, mode) : OpenAtNative(dir, name, flags, mode);
    // Apple ARM64 puts variadic arguments on the stack, unlike Linux ARM64 and Intel.
    // Filling the eight integer-register slots places openat's mode in that stack slot.
    // https://developer.apple.com/documentation/xcode/writing-arm64-code-for-apple-platforms
    [DllImport("libc", EntryPoint = "openat", SetLastError = true)] private static extern int OpenAtDarwinArm64(int dir, string name, int flags,
        nuint padding3, nuint padding4, nuint padding5, nuint padding6, nuint padding7, uint mode);
    [DllImport("libc", EntryPoint = "openat", SetLastError = true)] private static extern int OpenAtNative(int dir, string name, int flags, uint mode);
    [DllImport("libc", EntryPoint = "mkdirat", SetLastError = true)] private static extern int MkdirAt(int dir, string name, uint mode);
    [DllImport("libc", EntryPoint = "unlinkat", SetLastError = true)] private static extern int UnlinkAt(int dir, string name, int flags);
    [DllImport("libc", EntryPoint = "flock", SetLastError = true)] private static extern int Flock(SafeFileHandle handle, int flags);
    [DllImport("libc", EntryPoint = "fdopendir", SetLastError = true)] private static extern IntPtr FdOpenDir(int fd);
    [DllImport("libc", EntryPoint = "readdir", SetLastError = true)] private static extern IntPtr ReadDir(IntPtr dir);
    [DllImport("libc", EntryPoint = "closedir")] private static extern int CloseDir(IntPtr dir);
    [DllImport("libc", EntryPoint = "close")] private static extern int Close(int fd);
    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)] private static extern int Fstat(SafeFileHandle handle, out DarwinInfo info);
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)] private static extern int Statx(int fd, string path, int flags, uint mask, out LinuxInfo info);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileWindows(string path, uint access, uint share, ref SecurityAttributes security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateDirectoryWindows(string path, IntPtr security);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformation(SafeFileHandle handle, out WindowsInfo info);
    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetFileInformation(SafeFileHandle handle, int kind, ref byte information, uint size);
}
