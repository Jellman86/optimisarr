namespace Optimisarr.Tests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class PosixTheoryAttribute : TheoryAttribute
{
    public PosixTheoryAttribute()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            Skip = "Requires Linux/macOS POSIX paths. The server runs on Linux; Windows behaviour is tested separately.";
    }
}
