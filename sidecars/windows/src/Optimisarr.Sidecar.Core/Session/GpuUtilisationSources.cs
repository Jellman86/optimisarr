using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;

namespace Optimisarr.Sidecar.Core.Session;

/// <summary>Where a GPU busy figure can come from, and in which order they are tried.</summary>
public static class GpuUtilisationSources
{
    /// <summary>
    /// The Windows GPU Engine counters where the machine publishes them, which covers AMD, Intel
    /// and NVIDIA alike and matches what Task Manager shows; nvidia-smi only where it does not.
    ///
    /// <para>Counters that are present but still establishing their first window do not hand over
    /// to nvidia-smi, so one machine never alternates between two differently defined figures.</para>
    /// </summary>
    public static Func<double?> ReadComposite()
    {
        return OperatingSystem.IsWindows() ? CountersThen(ReadNvidiaUtilisation) : ReadNvidiaUtilisation;
    }

    [SupportedOSPlatform("windows")]
    private static Func<double?> CountersThen(Func<double?> fallback)
    {
        var counters = new WindowsGpuEngineCounters();
        return () =>
        {
            var fromCounters = counters.Read(out var supported);
            return supported ? fromCounters : fallback();
        };
    }

    /// <summary>
    /// GPU utilisation from nvidia-smi, or null on a machine without one.
    ///
    /// <para>A process call rather than NVML, because this runs every few seconds at most and a
    /// linked library would have to be found, versioned and kept working across driver updates for
    /// a number that is only ever displayed.</para>
    /// </summary>
    public static double? ReadNvidiaUtilisation()
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
