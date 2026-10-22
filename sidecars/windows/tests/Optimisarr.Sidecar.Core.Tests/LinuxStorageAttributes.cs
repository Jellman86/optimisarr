namespace Optimisarr.Sidecar.Core.Tests;

public sealed class LinuxStorageFactAttribute : FactAttribute
{
    public LinuxStorageFactAttribute() { if (!OperatingSystem.IsLinux()) Skip = "Requires native Linux persistent storage and file-descriptor ownership."; }
}
public sealed class LinuxStorageTheoryAttribute : TheoryAttribute
{
    public LinuxStorageTheoryAttribute() { if (!OperatingSystem.IsLinux()) Skip = "Requires native Linux persistent storage and file-descriptor ownership."; }
}
