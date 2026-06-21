using System.Globalization;

namespace Optimisarr.Core.Metrics;

/// <summary>
/// One DRM client's busy-time counters, parsed from a process's
/// <c>/proc/&lt;pid&gt;/fdinfo/&lt;fd&gt;</c>. The Linux DRM "fdinfo" interface exposes per-client
/// engine busy nanoseconds (<c>drm-engine-render</c>, <c>drm-engine-video</c>, …) and is
/// readable for a process owned by the same user — so Optimisarr can measure how hard its own
/// ffmpeg child is using the GPU without root, CAP_PERFMON, or the i915 perf interface that
/// <c>intel_gpu_top</c> needs.
/// </summary>
public sealed record DrmClientSample(long ClientId, string Driver, IReadOnlyDictionary<string, long> EngineNanos);

public static class DrmFdinfoParser
{
    private const string ClientIdKey = "drm-client-id:";
    private const string DriverKey = "drm-driver:";
    private const string EnginePrefix = "drm-engine-";

    /// <summary>
    /// Parses one fdinfo file's contents into a <see cref="DrmClientSample"/>, or null if the
    /// fd is not a DRM handle (no <c>drm-client-id</c>) or carries no engine counters. Engine
    /// lines look like <c>"drm-engine-video:\t98765432 ns"</c>.
    /// </summary>
    public static DrmClientSample? ParseClient(string? fdinfoText)
    {
        if (string.IsNullOrWhiteSpace(fdinfoText))
        {
            return null;
        }

        long? clientId = null;
        var driver = "";
        var engines = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var raw in fdinfoText.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith(ClientIdKey, StringComparison.Ordinal))
            {
                if (long.TryParse(ValueAfter(line, ClientIdKey), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                {
                    clientId = id;
                }
            }
            else if (line.StartsWith(DriverKey, StringComparison.Ordinal))
            {
                driver = ValueAfter(line, DriverKey).Trim();
            }
            else if (line.StartsWith(EnginePrefix, StringComparison.Ordinal))
            {
                var colon = line.IndexOf(':');
                if (colon <= EnginePrefix.Length)
                {
                    continue;
                }

                var engine = line[EnginePrefix.Length..colon];
                // The value is "<nanoseconds> ns"; take the leading number.
                var valuePart = line[(colon + 1)..].Trim();
                var firstToken = valuePart.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (long.TryParse(firstToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out var nanos) && nanos > 0)
                {
                    engines[engine] = nanos;
                }
            }
        }

        if (clientId is null || engines.Count == 0)
        {
            return null;
        }

        return new DrmClientSample(clientId.Value, driver, engines);
    }

    private static string ValueAfter(string line, string key) => line[key.Length..];
}

/// <summary>
/// Computes GPU engine utilisation between two aggregate snapshots of engine busy nanoseconds.
/// Each snapshot maps an engine name to its total busy-nanoseconds across all DRM clients (the
/// caller de-duplicates clients by id before summing, since a client's counters repeat across
/// its file descriptors).
/// </summary>
public static class DrmEngineUtilisation
{
    /// <summary>
    /// Returns the busiest engine's utilisation (0–100) and its name, or null engine when no
    /// engine advanced. Headlining the busiest engine matches how a transcode loads the GPU —
    /// the video engine does the codec work while the render engine handles any scaling/upload.
    /// </summary>
    public static (double Percent, string? Engine) Busiest(
        IReadOnlyDictionary<string, long> previous,
        IReadOnlyDictionary<string, long> current,
        double elapsedNanos)
    {
        if (elapsedNanos <= 0)
        {
            return (0, null);
        }

        double best = 0;
        string? engine = null;
        foreach (var (name, currentNanos) in current)
        {
            var previousNanos = previous.GetValueOrDefault(name);
            var deltaNanos = currentNanos - previousNanos;
            if (deltaNanos <= 0)
            {
                continue;
            }

            var percent = Math.Clamp(deltaNanos / elapsedNanos * 100.0, 0, 100);
            if (percent > best)
            {
                best = percent;
                engine = name;
            }
        }

        return (best, engine);
    }
}
