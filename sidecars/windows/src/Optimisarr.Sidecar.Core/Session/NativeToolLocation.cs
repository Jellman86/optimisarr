namespace Optimisarr.Sidecar.Core.Session;

/// <summary>Resolve hashable tool files while preserving an explicit override's intent.</summary>
internal static class NativeToolLocation
{
    public static string Resolve(string name, string packageRoot, string? configured, string? searchPath)
    {
        var executable = name + (OperatingSystem.IsWindows() ? ".exe" : "");
        var bundled = Path.Combine(packageRoot, executable);
        if (string.IsNullOrWhiteSpace(configured) && File.Exists(bundled)) return Path.GetFullPath(bundled);
        var requested = string.IsNullOrWhiteSpace(configured) ? executable : configured;
        if (File.Exists(requested)) return Path.GetFullPath(requested);
        if (Path.IsPathFullyQualified(requested) || requested.Contains(Path.DirectorySeparatorChar) || requested.Contains(Path.AltDirectorySeparatorChar))
            return requested;
        foreach (var directory in (searchPath ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, requested);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            if (OperatingSystem.IsWindows() && !candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(candidate + ".exe"))
                return Path.GetFullPath(candidate + ".exe");
        }
        return configured ?? bundled;
    }
}
