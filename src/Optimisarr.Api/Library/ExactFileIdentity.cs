using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Optimisarr.Api.Library;

/// <summary>Only supported filesystem identities permit metadata-based hash reuse.</summary>
internal static class ExactFileIdentity
{
    public static string? Read(string path)
    {
        try
        {
            if (OperatingSystem.IsLinux())
                return Statx(-100, path, 0, 0x100, out var linux) == 0 && (linux.Mask & 0x100) != 0 && linux.Inode != 0
                    ? $"linux:{linux.DeviceMajor}:{linux.DeviceMinor}:{linux.Inode}" : null;
            if (OperatingSystem.IsMacOS())
                return Stat(path, out var mac) == 0 && mac.Inode != 0 ? $"darwin:{mac.Device}:{mac.Inode}" : null;
            if (OperatingSystem.IsWindows())
            {
                using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                return GetFileInformationByHandle(handle, out var windows)
                    ? $"windows:{windows.VolumeSerial}:{windows.IndexHigh}:{windows.IndexLow}" : null;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException) { }
        return null;
    }

    public static string? Read(SafeFileHandle handle)
    {
        try
        {
            if (OperatingSystem.IsLinux())
                return Statx(handle.DangerousGetHandle().ToInt32(), "", 0x1000, 0x100, out var linux) == 0
                    && (linux.Mask & 0x100) != 0 && linux.Inode != 0
                    ? $"linux:{linux.DeviceMajor}:{linux.DeviceMinor}:{linux.Inode}" : null;
            if (OperatingSystem.IsMacOS())
                return Fstat(handle, out var mac) == 0 && mac.Inode != 0 ? $"darwin:{mac.Device}:{mac.Inode}" : null;
            if (OperatingSystem.IsWindows())
                return GetFileInformationByHandle(handle, out var windows)
                    ? $"windows:{windows.VolumeSerial}:{windows.IndexHigh}:{windows.IndexLow}" : null;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
        return null;
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxStat
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(32)] public ulong Inode;
        [FieldOffset(136)] public uint DeviceMajor;
        [FieldOffset(140)] public uint DeviceMinor;
    }
    [StructLayout(LayoutKind.Explicit, Size = 144)]
    private struct DarwinStat
    {
        [FieldOffset(0)] public int Device;
        [FieldOffset(8)] public ulong Inode;
    }
    [StructLayout(LayoutKind.Explicit, Size = 52)]
    private struct WindowsFileInfo
    {
        [FieldOffset(28)] public uint VolumeSerial;
        [FieldOffset(44)] public uint IndexHigh;
        [FieldOffset(48)] public uint IndexLow;
    }
    [DllImport("libc", EntryPoint = "statx", CharSet = CharSet.Ansi)]
    private static extern int Statx(int directoryFd, string path, int flags, uint mask, out LinuxStat result);
    [DllImport("libc", EntryPoint = "stat", CharSet = CharSet.Ansi)]
    private static extern int Stat(string path, out DarwinStat result);
    [DllImport("libc", EntryPoint = "fstat")]
    private static extern int Fstat(SafeFileHandle handle, out DarwinStat result);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out WindowsFileInfo result);
}
