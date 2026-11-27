using System.Security.Principal;

namespace Optimisarr.Sidecar.Core.Tests;

/// <summary>Real Windows file-system behaviour: skipped, and reported as skipped, anywhere else.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute() { if (!OperatingSystem.IsWindows()) Skip = "Requires Windows access control and reparse points."; }
}

/// <summary>
/// Needs an elevated run: only an administrator can give a file to Administrators, which is what
/// the service trusts. Skipped otherwise, so an ordinary run never reports it as passing.
/// </summary>
public sealed class WindowsElevatedFactAttribute : FactAttribute
{
    public WindowsElevatedFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Requires Windows access control and reparse points.";
        else if (!Elevated) Skip = "Requires an elevated (administrator) test run.";
    }

    public static bool Elevated
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return false;
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }
}
