namespace Optimisarr.Core.Workers;

/// <summary>Resolve ancestor links before identifying physical scratch and memory-backed storage.</summary>
public static class NativeWorkingVolume
{
    public static NativeWorkingResources? Read(string root, Func<long?> freeMemory, Func<string, long?>? freeDisk = null)
    {
        try
        {
            var full = PhysicalRoot(root);
            var drive = DriveInfo.GetDrives().Where(d => full == d.Name.TrimEnd(Path.DirectorySeparatorChar)
                || full.StartsWith(d.Name.EndsWith(Path.DirectorySeparatorChar) ? d.Name : d.Name + Path.DirectorySeparatorChar,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                .MaxBy(d => d.Name.Length);
            if (drive is null) return null;
            return new(drive.Name, freeDisk is null ? drive.AvailableFreeSpace : freeDisk(full),
                freeMemory(),
                drive.DriveType == DriveType.Ram || drive.DriveFormat is "tmpfs" or "ramfs");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or OverflowException)
        { return null; }
    }

    public static string PhysicalRoot(string root) => PhysicalRoot(root, 0);

    private static string PhysicalRoot(string root, int depth)
    {
        if (depth >= 40) throw new IOException("The working-space link chain is too deep.");
        var full = Path.GetFullPath(root);
        var physical = Path.GetPathRoot(full)!;
        foreach (var component in full[physical.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            physical = Path.Combine(physical, component);
            var directory = new DirectoryInfo(physical);
            if (directory.LinkTarget is not null)
            {
                // Resolve every ancestor: the workspace itself often does not exist yet, and
                // following only its leaf would hide an alias into a memory-backed volume.
                var target = directory.ResolveLinkTarget(returnFinalTarget: true);
                if (target is not DirectoryInfo resolved || !resolved.Exists)
                    throw new IOException("The working-space link has no accessible directory target.");
                physical = PhysicalRoot(resolved.FullName, depth + 1);
            }
        }
        return physical;
    }

}
