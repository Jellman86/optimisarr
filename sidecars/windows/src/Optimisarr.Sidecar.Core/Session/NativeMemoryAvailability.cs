using System.Runtime.InteropServices;

namespace Optimisarr.Sidecar.Core.Session;

/// <summary>Live physical/cgroup availability, never a stale GC load snapshot presented as free RAM.</summary>
internal static class NativeMemoryAvailability
{
    internal static long? Read()
    {
        try
        {
            long? available;
            if (OperatingSystem.IsWindows())
            {
                var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
                available = GlobalMemoryStatusEx(ref status) && status.AvailablePhysical <= long.MaxValue ? (long)status.AvailablePhysical : null;
                if (available is null || !IsProcessInJob(new IntPtr(-1), IntPtr.Zero, out var inJob)) return null;
                if (inJob)
                {
                    if (!QueryInformationJobObject(IntPtr.Zero, 9, out var limits, (uint)Marshal.SizeOf<JobLimits>(), IntPtr.Zero)) return null;
                    // Peak job usage is conservative and includes native children. A GC heap
                    // allowance is not a native-process limit and must not substitute for this.
                    available = ApplyWindowsLimits(available.Value, limits.Basic.Flags, limits.ProcessMemory.ToUInt64(),
                        limits.JobMemory.ToUInt64(), limits.PeakJobMemory.ToUInt64());
                }
            }
            else if (OperatingSystem.IsLinux())
            {
                available = Optimisarr.Core.Tools.LinuxMemoryAvailability.ReadCurrent();
            }
            else return null;
            return available;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return null; }
    }

    internal static long? ApplyWindowsLimits(long available, uint flags, ulong process, ulong job, ulong peak)
    {
        if ((flags & 0x100) != 0)
        {
            if (process > long.MaxValue) return null;
            available = Math.Min(available, (long)process);
        }
        if ((flags & 0x200) != 0)
        {
            if (job > long.MaxValue) return null;
            available = Math.Min(available, peak >= job ? 0 : (long)(job - peak));
        }
        return available;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicJobLimits
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcesses;
        public UIntPtr Affinity;
        public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct JobLimits
    {
        public BasicJobLimits Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(IntPtr process, IntPtr job, [MarshalAs(UnmanagedType.Bool)] out bool inJob);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(IntPtr job, int informationClass, out JobLimits information, uint length, IntPtr returnedLength);
}
