using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Optimisarr.Sidecar.Core.Session;

/// <summary>
/// A reading of the machine's cumulative CPU time, as Windows counts it.
///
/// <para>Cumulative rather than instantaneous: the kernel counts since boot, so one reading says
/// nothing on its own and two a moment apart say everything.</para>
/// </summary>
public readonly record struct CpuTicks(ulong Idle, ulong Kernel, ulong User)
{
    // Windows folds idle time into the kernel total, so kernel alone overstates busyness badly on
    // an idle machine — subtracting idle is not an optimisation, it is the difference between 4%
    // and 96%.
    public ulong Busy => Kernel + User - Idle;

    public ulong Total => Kernel + User;
}

public static class CpuLoadCalculator
{
    /// <summary>
    /// The fraction of the interval between two readings spent doing something, 0 to 1.
    ///
    /// <para>Null rather than a number wherever one would be a lie: no time passed, or the counters
    /// went backwards because the machine slept or the values wrapped. A sidecar reporting "0% busy"
    /// for those cases would read as an idle machine, which is a different claim entirely from
    /// "cannot say" — and the wrong one to put in front of someone deciding where to send work.</para>
    /// </summary>
    public static double? BusyFraction(CpuTicks previous, CpuTicks current)
    {
        if (current.Total < previous.Total || current.Busy < previous.Busy)
        {
            return null;
        }

        var total = current.Total - previous.Total;
        return total == 0 ? null : Math.Clamp((double)(current.Busy - previous.Busy) / total, 0, 1);
    }
}

/// <summary>
/// Takes both readings together, so a reported pair describes one moment.
///
/// <para>Each consumer holds its own: the check-in loop and a job's lease renewals run at different
/// cadences, and a CPU figure is the busy fraction <em>since that sampler last looked</em>. Sharing
/// one would leave each caller reporting whatever window the other happened to leave behind.</para>
/// </summary>
public sealed class MachineLoadSampler(
    Func<CpuTicks?>? readCpu = null,
    Func<double?>? readGpu = null)
{
    private readonly Lock _gate = new();
    private readonly Func<CpuTicks?> _readCpu = readCpu ?? ReadSystemTimes;
    private readonly Func<double?> _readGpu = readGpu ?? ReadNvidiaUtilisation;
    private CpuTicks? _previous;

    /// <summary>Null when neither figure could be read, so a caller sends nothing rather than a body of absences.</summary>
    public MachineLoad? Sample()
    {
        var load = new MachineLoad(SampleCpu(), _readGpu());
        return load.IsEmpty ? null : load;
    }

    private double? SampleCpu()
    {
        var current = _readCpu();
        if (current is not { } reading)
        {
            return null;
        }

        lock (_gate)
        {
            var previous = _previous;
            _previous = reading;
            // The first reading has nothing to compare against. That is "cannot say", not "idle".
            return previous is { } earlier ? CpuLoadCalculator.BusyFraction(earlier, reading) : null;
        }
    }

    // DllImport rather than the source-generated LibraryImport: the generator requires
    // AllowUnsafeBlocks across the whole project, which is a large change to its safety posture in
    // exchange for one call that marshals three longs.
    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    private static CpuTicks? ReadSystemTimes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        return GetSystemTimes(out var idle, out var kernel, out var user)
            ? new CpuTicks((ulong)idle, (ulong)kernel, (ulong)user)
            : null;
    }

    /// <summary>
    /// GPU utilisation from nvidia-smi, or null on a machine without one.
    ///
    /// <para>A process call rather than NVML, because this runs every few seconds at most and a
    /// linked library would have to be found, versioned and kept working across driver updates for
    /// a number that is only ever displayed.</para>
    /// </summary>
    private static double? ReadNvidiaUtilisation()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("nvidia-smi")
            {
                ArgumentList = { "--query-gpu=utilization.gpu", "--format=csv,noheader,nounits" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadLine();
            if (!process.WaitForExit(2000))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            return double.TryParse(output?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var percent)
                ? Math.Clamp(percent / 100, 0, 1)
                : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // No NVIDIA card, no driver, or nvidia-smi not on PATH. Reporting nothing is correct:
            // this machine genuinely cannot say what its accelerator is doing.
            return null;
        }
    }
}
