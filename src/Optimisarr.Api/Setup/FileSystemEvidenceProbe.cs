namespace Optimisarr.Api.Setup;

internal sealed record LinuxMount(
    string MountId,
    string FileSystemId,
    string MountPoint,
    string FileSystemType);

internal sealed record FileSystemEvidence(
    string? FileSystemId,
    string? MountId,
    string? MountPoint,
    string? FileSystemType,
    long? AvailableBytes,
    long? TotalBytes);

internal static class FileSystemEvidenceProbe
{
    public static FileSystemEvidence Probe(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return new FileSystemEvidence(null, null, null, null, null, null);
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or PathTooLongException)
        {
            return new FileSystemEvidence(null, null, null, null, null, null);
        }

        var linuxMount = OperatingSystem.IsLinux() ? TryFindLinuxMount(fullPath) : null;
        var drive = FindDrive(fullPath);

        return new FileSystemEvidence(
            linuxMount?.FileSystemId ?? drive?.Name,
            linuxMount?.MountId ?? drive?.Name,
            linuxMount?.MountPoint ?? drive?.Name,
            linuxMount?.FileSystemType ?? TryReadString(() => drive?.DriveFormat),
            TryReadLong(() => drive?.AvailableFreeSpace),
            TryReadLong(() => drive?.TotalSize));
    }

    public static bool SharesAtomicBoundary(FileSystemEvidence first, FileSystemEvidence second) =>
        !string.IsNullOrWhiteSpace(first.MountId)
        && string.Equals(first.MountId, second.MountId, StringComparison.Ordinal);

    internal static LinuxMount? FindLinuxMount(string path, string mountInfo)
    {
        LinuxMount? best = null;
        foreach (var line in mountInfo.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = line.IndexOf(" - ", StringComparison.Ordinal);
            if (separator < 0)
            {
                continue;
            }

            var left = line[..separator].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var right = line[(separator + 3)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (left.Length < 5 || right.Length < 1)
            {
                continue;
            }

            var mountPoint = DecodeMountInfoField(left[4]);
            if (!IsWithin(path, mountPoint) || best is not null && mountPoint.Length <= best.MountPoint.Length)
            {
                continue;
            }

            best = new LinuxMount(left[0], left[2], mountPoint, right[0]);
        }

        return best;
    }

    private static LinuxMount? TryFindLinuxMount(string path)
    {
        try
        {
            const string mountInfoPath = "/proc/self/mountinfo";
            return File.Exists(mountInfoPath)
                ? FindLinuxMount(path, File.ReadAllText(mountInfoPath))
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static DriveInfo? FindDrive(string path)
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(drive => drive.IsReady && IsWithin(path, drive.Name))
                .OrderByDescending(drive => drive.Name.Length)
                .FirstOrDefault();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static long? TryReadLong(Func<long?> read)
    {
        try
        {
            return read();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? TryReadString(Func<string?> read)
    {
        try
        {
            return read();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsWithin(string path, string root)
    {
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var normalizedPath = Path.TrimEndingDirectorySeparator(path);
        var normalizedRoot = Path.TrimEndingDirectorySeparator(root);
        if (string.Equals(normalizedPath, normalizedRoot, comparison))
        {
            return true;
        }

        var prefix = normalizedRoot == Path.DirectorySeparatorChar.ToString()
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(prefix, comparison);
    }

    private static string DecodeMountInfoField(string value) => value
        .Replace("\\040", " ", StringComparison.Ordinal)
        .Replace("\\011", "\t", StringComparison.Ordinal)
        .Replace("\\012", "\n", StringComparison.Ordinal)
        .Replace("\\134", "\\", StringComparison.Ordinal);
}

public enum SetupPathIssue
{
    None,
    Missing,
    Unreadable,
    Unwritable,
    LowSpace
}

internal static class SetupPathIssueClassifier
{
    public static SetupPathIssue Classify(
        bool exists,
        bool readable,
        bool writable,
        long? availableBytes,
        long? requiredBytes)
    {
        if (!exists)
        {
            return SetupPathIssue.Missing;
        }

        if (!readable)
        {
            return SetupPathIssue.Unreadable;
        }

        if (!writable)
        {
            return SetupPathIssue.Unwritable;
        }

        return availableBytes is not null && requiredBytes is not null && availableBytes < requiredBytes
            ? SetupPathIssue.LowSpace
            : SetupPathIssue.None;
    }

    public static string ToWireValue(SetupPathIssue issue) => issue switch
    {
        SetupPathIssue.None => "none",
        SetupPathIssue.Missing => "missing",
        SetupPathIssue.Unreadable => "unreadable",
        SetupPathIssue.Unwritable => "unwritable",
        SetupPathIssue.LowSpace => "lowSpace",
        _ => throw new ArgumentOutOfRangeException(nameof(issue), issue, null)
    };
}

internal enum DeploymentPlatform
{
    Local,
    Compose,
    Unraid,
    TrueNas
}

internal static class DeploymentPlatformDetector
{
    public static DeploymentPlatform Detect(IReadOnlyDictionary<string, string?> variables)
    {
        if (variables.TryGetValue("HOST_OS", out var hostOs)
            && string.Equals(hostOs, "Unraid", StringComparison.OrdinalIgnoreCase))
        {
            return DeploymentPlatform.Unraid;
        }

        if (variables.Keys.Any(key => key.StartsWith("IX_", StringComparison.OrdinalIgnoreCase))
            || variables.Keys.Any(key => key.Contains("TRUENAS", StringComparison.OrdinalIgnoreCase)))
        {
            return DeploymentPlatform.TrueNas;
        }

        return variables.TryGetValue("DOTNET_RUNNING_IN_CONTAINER", out var inContainer)
            && string.Equals(inContainer, "true", StringComparison.OrdinalIgnoreCase)
                ? DeploymentPlatform.Compose
                : DeploymentPlatform.Local;
    }

    public static DeploymentPlatform DetectCurrent() => Detect(Environment.GetEnvironmentVariables()
        .Cast<System.Collections.DictionaryEntry>()
        .ToDictionary(entry => (string)entry.Key, entry => entry.Value?.ToString()));

    public static string ToWireValue(DeploymentPlatform platform) => platform switch
    {
        DeploymentPlatform.Local => "local",
        DeploymentPlatform.Compose => "compose",
        DeploymentPlatform.Unraid => "unraid",
        DeploymentPlatform.TrueNas => "truenas",
        _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, null)
    };
}
