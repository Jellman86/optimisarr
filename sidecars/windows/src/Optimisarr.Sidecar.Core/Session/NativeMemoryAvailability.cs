using System.Globalization;
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
                var limits = new List<(string Limit, string Used)>();
                var unified = File.ReadAllLines("/proc/self/cgroup").FirstOrDefault(line => line.StartsWith("0::", StringComparison.Ordinal));
                if (unified is null) return null; // A missing resource controller is unknown capacity.
                const string root = "/sys/fs/cgroup";
                var directory = Path.GetFullPath(Path.Combine(root, unified[3..].TrimStart('/')));
                if (directory != root && !directory.StartsWith(root + "/", StringComparison.Ordinal)) return null;
                // A cgroup namespace can expose its own root rather than the host-relative path.
                if (!Directory.Exists(directory)) directory = root;
                while (true)
                {
                    var limit = Path.Combine(directory, "memory.max");
                    if (File.Exists(limit)) limits.Add((File.ReadAllText(limit), File.ReadAllText(Path.Combine(directory, "memory.current"))));
                    if (directory == root) break;
                    directory = Path.GetDirectoryName(directory)!;
                }
                available = ParseLinux(File.ReadAllText("/proc/meminfo"), limits);
            }
            else return null;
            return available;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return null; }
    }

    internal static long? ParseLinux(string meminfo, IEnumerable<(string Limit, string Used)> limits)
    {
        var rows = meminfo.Split('\n').Where(line => line.StartsWith("MemAvailable:", StringComparison.Ordinal)).ToArray();
        if (rows.Length != 1) return null;
        var fields = rows[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 3 || fields[2] != "kB" || !Number(fields[1], out var kib) || kib > long.MaxValue / 1024) return null;
        var available = kib * 1024;
        foreach (var (limit, used) in limits)
        {
            if (limit.Trim() == "max") continue;
            if (!Number(limit.Trim(), out var maximum) || !Number(used.Trim(), out var current)) return null;
            available = Math.Min(available, Math.Max(0, maximum - current));
        }
        return available;
    }
    private static bool Number(string text, out long value) => long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);

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
