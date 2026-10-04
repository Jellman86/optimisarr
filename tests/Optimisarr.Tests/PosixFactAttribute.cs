namespace Optimisarr.Tests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class PosixFactAttribute : FactAttribute
{
    public PosixFactAttribute()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            Skip = "Requires Linux/macOS POSIX paths or native hard-link probes. Windows sidecar behavior is tested separately.";
    }
}
