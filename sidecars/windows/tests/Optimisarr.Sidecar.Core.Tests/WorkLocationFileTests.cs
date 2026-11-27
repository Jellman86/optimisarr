using System.Security.AccessControl;
using System.Security.Principal;
using Optimisarr.Sidecar.Service;

namespace Optimisarr.Sidecar.Core.Tests;

/// <summary>
/// How the service reads the work-location override file on Windows: absent means not configured,
/// and a file an ordinary account made or can change is refused rather than followed.
/// </summary>
public sealed class WorkLocationFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "optimisarr-work-location", Guid.NewGuid().ToString("N"));

    public WorkLocationFileTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void An_absent_file_means_not_configured()
    {
        if (!OperatingSystem.IsWindows()) return;

        Assert.Equal((null, null), Program.ReadOverrideFile(Path.Combine(_root, "work-location")));
        Assert.Equal((null, null), Program.ReadOverrideFile(Path.Combine(_root, "missing", "work-location")));
    }

    [Fact]
    public void A_file_the_current_user_made_is_not_trusted_unless_administrators_own_it()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(_root, "work-location");
        File.WriteAllText(path, @"D:\OptimisarrWork");

        var (configured, problem) = Program.ReadOverrideFile(path);

        // An elevated test run creates files owned by Administrators but still grants the user's own
        // inherited access from the temporary folder; either way an ordinary account can change it.
        Assert.Null(configured);
        Assert.NotNull(problem);
        Assert.Contains("not trusted", problem);
    }

    [Fact]
    public void A_file_only_administrators_and_system_control_is_read()
    {
        if (!OperatingSystem.IsWindows() || !Elevated()) return;
        var path = Path.Combine(_root, "work-location");
        File.WriteAllText(path, "\uFEFFR:\\OptimisarrWork\r\n");
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);

        Assert.Equal((@"R:\OptimisarrWork", null), Program.ReadOverrideFile(path));
    }

    private static bool Elevated()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
