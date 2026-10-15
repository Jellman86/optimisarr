using System.Globalization;

namespace Optimisarr.Core.Tools;

/// <summary>Physical RAM bounded by the process's visible memory-controller hierarchy.</summary>
public static class LinuxMemoryAvailability
{
    public static long? ReadCurrent()
    {
        if (!OperatingSystem.IsLinux()) return null;
        try
        {
            return Read(File.ReadAllText("/proc/meminfo"), File.ReadAllText("/proc/self/cgroup"),
                File.ReadAllText("/proc/self/mountinfo"),
                path => File.Exists(path) ? File.ReadAllText(path) : null, Directory.Exists);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { return null; }
    }

    public static long? Read(string meminfo, string membership, string mounts,
        Func<string, string?> read, Func<string, bool> directoryExists)
    {
        var available = Physical(meminfo);
        if (available is null) return null;
        var groups = membership.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split(':', 3)).ToArray();
        if (groups.Any(row => row.Length != 3 || !Number(row[0], out _))) return null;
        var legacy = groups.Where(row => row[1].Split(',').Contains("memory", StringComparer.Ordinal)).ToArray();
        var unified = groups.Where(row => row[0] == "0" && row[1] == "").ToArray();
        if (legacy.Length > 1 || unified.Length > 1) return null;
        var group = legacy.Length == 1 ? legacy[0] : unified.SingleOrDefault();
        if (group is null || !SafePath(group[2])) return null;
        var v1 = legacy.Length == 1;
        var found = false;
        foreach (var line in mounts.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var halves = line.Split(" - ", 2, StringSplitOptions.None);
            if (halves.Length != 2) return null;
            var before = halves[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var after = halves[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (before.Length < 6 || after.Length < 3) return null;
            if (v1 ? after[0] != "cgroup" || !after[2].Split(',').Contains("memory", StringComparer.Ordinal) : after[0] != "cgroup2") continue;
            var mountRoot = DecodePath(before[3]);
            var mountPoint = DecodePath(before[4]);
            if (mountRoot is null || mountPoint is null) return null;
            // proc membership is relative to the cgroup namespace; mountinfo can still
            // name its host subtree. Only an explicit namespace root maps this way.
            var relative = group[2] == "/" ? "" : mountRoot == "/" ? group[2]
                : group[2] == mountRoot ? "" : group[2].StartsWith(mountRoot + "/", StringComparison.Ordinal) ? group[2][mountRoot.Length..] : null;
            if (relative is null) continue;
            var current = mountPoint == "/" ? "/" + relative.TrimStart('/') : mountPoint + relative;
            if (!directoryExists(current)) continue;
            found = true;
            while (true)
            {
                var prefix = current == "/" ? "/" : current + "/";
                var limit = read(prefix + (v1 ? "memory.limit_in_bytes" : "memory.max"));
                var used = read(prefix + (v1 ? "memory.usage_in_bytes" : "memory.current"));
                var high = v1 ? null : read(prefix + "memory.high");
                if (limit is not null || used is not null || high is not null)
                {
                    if (used is null || !Number(used.Trim(), out var bytes)) return null;
                    // The v2 hierarchy root may expose current usage without limit controls.
                    if (limit is null && (v1 || current != mountPoint)) return null;
                    foreach (var bound in new[] { limit, high })
                    {
                        if (bound is null || !v1 && bound.Trim() == "max") continue;
                        if (!Number(bound.Trim(), out var maximum)) return null;
                        available = Math.Min(available.Value, bytes >= maximum ? 0 : maximum - bytes);
                    }
                }
                if (current == mountPoint) break;
                var slash = current.LastIndexOf('/');
                current = slash == 0 ? "/" : current[..slash];
            }
        }
        return found ? available : null;
    }

    public static long? ApplyLimits(string meminfo, IEnumerable<(string Limit, string Used)> limits)
    {
        var available = Physical(meminfo);
        if (available is null) return null;
        foreach (var (limit, used) in limits)
        {
            if (!Number(used.Trim(), out var bytes)) return null;
            if (limit.Trim() == "max") continue;
            if (!Number(limit.Trim(), out var maximum)) return null;
            available = Math.Min(available.Value, bytes >= maximum ? 0 : maximum - bytes);
        }
        return available;
    }

    private static long? Physical(string meminfo)
    {
        var rows = meminfo.Split('\n').Where(line => line.StartsWith("MemAvailable:", StringComparison.Ordinal)).ToArray();
        if (rows.Length != 1) return null;
        var fields = rows[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return fields.Length == 3 && fields[2] == "kB" && Number(fields[1], out var kib) && kib <= long.MaxValue / 1024 ? kib * 1024 : null;
    }

    private static string? DecodePath(string value)
    {
        var decoded = value.Replace("\\040", " ", StringComparison.Ordinal).Replace("\\011", "\t", StringComparison.Ordinal)
            .Replace("\\012", "\n", StringComparison.Ordinal).Replace("\\134", "\\", StringComparison.Ordinal);
        return SafePath(decoded) ? decoded : null;
    }
    private static bool SafePath(string value) => value.StartsWith('/') && (value == "/"
        || !value.EndsWith('/') && value.Split('/').Skip(1).All(part => part.Length > 0 && part is not ("." or "..") && !part.Contains('\0')));
    private static bool Number(string value, out long number) => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number);
}
