using System.Runtime.InteropServices;

namespace Optimisarr.Sidecar.Core.Session;

/// <summary>Live physical/cgroup availability, with unknown Windows job ancestry held safely.</summary>
internal static class NativeMemoryAvailability
{
    internal static long? Read()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
                if (!GlobalMemoryStatusEx(ref status) || !IsProcessInJob(new IntPtr(-1), IntPtr.Zero, out var inJob)) return null;
                return WindowsCapacity(status.AvailablePhysical, inJob);
            }
            return OperatingSystem.IsLinux() ? Optimisarr.Core.Tools.LinuxMemoryAvailability.ReadCurrent() : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return null; }
    }

    internal static long? WindowsCapacity(ulong physical, bool? inJob)
    {
        // Current-job and app-memory queries do not prove stricter ancestor limits.
        // Without the complete chain, even an apparently unlimited
        // job cannot prove native capacity. Hold rather than spending host or inner-job RAM.
        return inJob == false && physical <= long.MaxValue ? (long)physical : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(IntPtr process, IntPtr job, [MarshalAs(UnmanagedType.Bool)] out bool inJob);
}
