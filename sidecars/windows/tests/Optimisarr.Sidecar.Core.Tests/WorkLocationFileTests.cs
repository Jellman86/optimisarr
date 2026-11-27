using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Optimisarr.Sidecar.Core.Session;
using Optimisarr.Sidecar.Service;

namespace Optimisarr.Sidecar.Core.Tests;

/// <summary>
/// How the service reads the work-location override file on Windows: absent means not configured,
/// and a file an ordinary account made, can change, or reaches through a link is refused rather
/// than followed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WorkLocationFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "optimisarr-work-location", Guid.NewGuid().ToString("N"));

    public WorkLocationFileTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        // Links first, as links: a recursive delete must never be what cleans up a junction.
        foreach (var entry in new DirectoryInfo(_root).EnumerateFileSystemInfos())
            if (entry.LinkTarget is not null) entry.Delete();
        Directory.Delete(_root, recursive: true);
    }

    private string FilePath => Path.Combine(_root, "work-location");

    [WindowsFact]
    public void An_absent_file_means_not_configured()
    {
        Assert.Equal((null, null), Program.ReadOverrideFile(FilePath));
        Assert.Equal((null, null), Program.ReadOverrideFile(Path.Combine(_root, "missing", "work-location")));
    }

    [WindowsFact]
    public void A_file_the_current_user_made_is_not_trusted_unless_administrators_own_it()
    {
        File.WriteAllText(FilePath, @"D:\OptimisarrWork");

        var (configured, problem) = Program.ReadOverrideFile(FilePath);

        // An elevated test run creates files owned by Administrators but still grants the user's own
        // inherited access from the temporary folder; either way an ordinary account can change it.
        Assert.Null(configured);
        Assert.Contains("not trusted", problem);
    }

    [WindowsFact]
    public void A_file_far_longer_than_one_path_is_refused_before_its_access_is_judged()
    {
        File.WriteAllText(FilePath, new string('x', WorkLocation.MaximumOverrideFileBytes + 1));

        var (configured, problem) = Program.ReadOverrideFile(FilePath);

        Assert.Null(configured);
        Assert.Contains("far longer", problem);
    }

    [WindowsFact]
    public void A_link_in_place_of_the_file_is_refused_without_being_followed()
    {
        var target = Directory.CreateDirectory(Path.Combine(_root, "elsewhere")).FullName;
        WindowsLinks.Junction(FilePath, target);

        var (configured, problem) = Program.ReadOverrideFile(FilePath);

        Assert.Null(configured);
        Assert.Contains("link", problem);
    }

    [WindowsFact]
    public void A_file_with_a_second_hard_link_is_refused()
    {
        // A second name in a folder an ordinary account controls would let it rewrite the file.
        var other = Path.Combine(_root, "other-name");
        File.WriteAllText(other, @"D:\OptimisarrWork");
        WindowsLinks.HardLink(FilePath, other);

        var (configured, problem) = Program.ReadOverrideFile(FilePath);

        Assert.Null(configured);
        Assert.Contains("hard link", problem);
    }

    [WindowsElevatedFact]
    public void A_file_only_administrators_and_system_control_is_read()
    {
        File.WriteAllText(FilePath, "\uFEFFR:\\OptimisarrWork\r\n");
        LockToAdministrators(FilePath);

        Assert.Equal((@"R:\OptimisarrWork", null), Program.ReadOverrideFile(FilePath));
    }

    [WindowsElevatedFact]
    public void A_trusted_file_saved_as_utf16_is_read()
    {
        File.WriteAllText(FilePath, "R:\\Arbeit\u00E9\r\n", Encoding.Unicode);
        LockToAdministrators(FilePath);

        Assert.Equal(("R:\\Arbeit\u00E9", null), Program.ReadOverrideFile(FilePath));
    }

    private static void LockToAdministrators(string path)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }
}
