using System.Diagnostics;
using System.Runtime.Versioning;

namespace Optimisarr.Sidecar.Core.Session;

/// <summary>
/// GPU busyness from the <c>GPU Engine</c> performance counters Windows publishes for every
/// vendor's WDDM driver, so an AMD or Intel machine reports what its encoder is doing without a
/// vendor tool.
///
/// <para>One category read per call, not one counter object per instance: a machine has hundreds
/// of engine instances, and building a counter for each took seconds. The utilisation counter is
/// a rate, so each call compares with the previous call's raw samples; the figure is the average
/// since the last read, and the first read has nothing to compare against.</para>
///
/// <para>Not thread-safe; <see cref="BackgroundGpuReading"/> never runs two reads at once.</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsGpuEngineCounters
{
    private const string Category = "GPU Engine";
    private const string Utilisation = "Utilization Percentage";

    private Dictionary<string, CounterSample> _previous = new(StringComparer.Ordinal);

    /// <summary>
    /// Null when the counters are missing, unreadable or have no earlier sample yet.
    /// <paramref name="supported"/> says which: only a machine without readable counters should
    /// fall back to another source, not one whose first read is still establishing its window.
    /// </summary>
    public double? Read(out bool supported)
    {
        supported = false;
        try
        {
            if (!PerformanceCounterCategory.Exists(Category))
            {
                return null;
            }

            var samples = new PerformanceCounterCategory(Category).ReadCategory()[Utilisation];
            if (samples is null)
            {
                return null;
            }

            supported = true;

            var previous = _previous;
            var current = new Dictionary<string, CounterSample>(StringComparer.Ordinal);
            var readings = new List<(string Instance, double Percent)>();
            foreach (InstanceData instance in samples.Values)
            {
                if (!GpuEngineUtilisation.IsRelevantEngine(instance.InstanceName))
                {
                    continue;
                }

                current[instance.InstanceName] = instance.Sample;
                // A process that started since the last read has no window yet; it is counted from
                // the next one rather than guessed at now.
                if (previous.TryGetValue(instance.InstanceName, out var earlier))
                {
                    readings.Add((instance.InstanceName, CounterSample.Calculate(earlier, instance.Sample)));
                }
            }

            _previous = current;
            return GpuEngineUtilisation.BusiestEngine(readings);
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or PlatformNotSupportedException or FormatException)
        {
            // Counter provider missing, disabled or corrupt. Forget the old window so a recovered
            // provider starts cleanly, and report nothing rather than a stale or invented figure.
            _previous = new(StringComparer.Ordinal);
            supported = false;
            return null;
        }
    }
}
