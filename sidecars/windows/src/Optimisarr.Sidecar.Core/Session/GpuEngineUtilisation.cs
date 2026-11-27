namespace Optimisarr.Sidecar.Core.Session;

/// <summary>
/// Turns Windows <c>GPU Engine</c> counter readings into one busy fraction, the way Task Manager
/// does.
///
/// <para>Windows publishes one counter instance per process per engine, named like
/// <c>pid_1234_luid_0x00000000_0x0000D1B8_phys_0_eng_3_engtype_VideoEncode</c>. An engine's
/// busyness is the sum of what every process spent on it, so two encodes sharing one encoder
/// engine add up rather than each reporting half. The machine figure is then the busiest engine
/// on any adapter: the same "is any part of the GPU saturated" reading nvidia-smi gives, and the
/// one Task Manager shows per GPU.</para>
/// </summary>
public static class GpuEngineUtilisation
{
    private const string AdapterMarker = "luid_";
    private const string EngineTypeMarker = "_engtype_";

    /// <summary>
    /// The busiest relevant engine, 0 to 1. Null when no reading named an engine a transcode can
    /// use, because "nothing to measure" is not the same claim as "idle".
    /// </summary>
    public static double? BusiestEngine(IEnumerable<(string Instance, double Percent)> readings)
    {
        var engines = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (instance, percent) in readings)
        {
            if (!double.IsFinite(percent) || Engine(instance) is not { } engine || !IsRelevantType(engine.Type))
            {
                continue;
            }

            engines[engine.Key] = engines.GetValueOrDefault(engine.Key) + Math.Max(0, percent);
        }

        return engines.Count == 0 ? null : Math.Clamp(engines.Values.Max() / 100, 0, 1);
    }

    /// <summary>Whether a counter instance names an engine an encode, decode or filter can occupy.</summary>
    internal static bool IsRelevantEngine(string instance) =>
        Engine(instance) is { } engine && IsRelevantType(engine.Type);

    /// <summary>
    /// The engine an instance belongs to — adapter, physical GPU and engine index, without the
    /// process — and its type. Null for a name in any other shape, so a provider that changes its
    /// naming yields "unavailable" rather than a number built from misread parts.
    /// </summary>
    private static (string Key, string Type)? Engine(string instance)
    {
        var adapter = instance.IndexOf(AdapterMarker, StringComparison.OrdinalIgnoreCase);
        var type = instance.IndexOf(EngineTypeMarker, StringComparison.OrdinalIgnoreCase);
        if (adapter < 0 || type <= adapter)
        {
            return null;
        }

        var engineType = instance[(type + EngineTypeMarker.Length)..];
        return engineType.Length == 0 ? null : (instance[adapter..type], engineType);
    }

    /// <summary>
    /// Drivers name their engines differently — <c>VideoEncode</c>, <c>Video Codec_0</c>,
    /// <c>Compute_1</c>, <c>Cuda</c> — so the type is compared without spacing, separators or an
    /// index. Encoders, decoders and video processing are the work itself; 3D, compute and copy
    /// engines carry hardware filters and frame transfers. Security, timer, audio and overlay
    /// engines never move during a transcode and are left out.
    /// </summary>
    private static bool IsRelevantType(string engineType)
    {
        var normalised = new string(engineType
            .Where(character => character is not (' ' or '_'))
            .Select(char.ToLowerInvariant)
            .ToArray())
            .TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        return normalised.StartsWith("video", StringComparison.Ordinal)
            || normalised is "3d" or "cuda" or "copy"
            || normalised.Contains("compute", StringComparison.Ordinal);
    }
}
