using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Core.Tests;

/// <summary>
/// The work folder and job folders against real Windows access lists and junctions. Junctions need
/// no privilege, so these are the moves an ordinary account on the machine could make.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsWorkRootTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "optimisarr-work-root", Guid.NewGuid().ToString("N"));

    public WindowsWorkRootTests() => Directory.CreateDirectory(_temp);

    public void Dispose() => RemoveWithoutFollowing(new DirectoryInfo(_temp));

    private string Outside()
    {
        var outside = Directory.CreateDirectory(Path.Combine(_temp, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "precious"), "keep");
        return outside;
    }

    [WindowsElevatedFact]
    public void A_missing_work_folder_is_created_for_system_and_administrators_only()
    {
        var path = Path.Combine(_temp, "work");

        using var root = WindowsWorkRoot.Open(path, allowUnprotected: false, mayChange: true);

        Assert.Null(root.Problem);
        var security = new DirectoryInfo(path).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        var principals = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Select(rule => rule.IdentityReference.Value).Distinct().Order().ToArray();
        Assert.Equal(new[] { WorkAccess.LocalSystem, WorkAccess.Administrators }.Order().ToArray(), principals);
        Assert.True(WorkAccess.IsTrusted(security.GetOwner(typeof(SecurityIdentifier))?.Value));
    }

    [WindowsFact]
    public void A_work_folder_that_is_a_junction_is_refused_and_its_target_left_alone()
    {
        var outside = Outside();
        var path = Path.Combine(_temp, "work");
        WindowsLinks.Junction(path, outside);

        using var root = WindowsWorkRoot.Open(path, allowUnprotected: false, mayChange: true);

        Assert.Contains("link", root.Problem);
        Assert.False(new DirectoryInfo(outside).GetAccessControl().AreAccessRulesProtected);
        Assert.True(File.Exists(Path.Combine(outside, "precious")));
    }

    [WindowsFact]
    public void A_work_folder_beneath_a_junction_is_refused()
    {
        var outside = Outside();
        var parent = Path.Combine(_temp, "parent");
        WindowsLinks.Junction(parent, outside);

        using var root = WindowsWorkRoot.Open(Path.Combine(parent, "work"), allowUnprotected: false, mayChange: true);

        Assert.Contains(parent, root.Problem);
        Assert.False(Directory.Exists(Path.Combine(outside, "work")));
    }

    [WindowsElevatedFact]
    public void A_hand_made_work_folder_other_accounts_can_change_is_refused_and_left_as_it_is()
    {
        // Made by hand, so owned by Administrators or by the user who ran this, and carrying the
        // temporary folder's inherited access for that user: not the service's folder to rewrite.
        var path = Directory.CreateDirectory(Path.Combine(_temp, "work")).FullName;

        using var root = WindowsWorkRoot.Open(path, allowUnprotected: false, mayChange: true);

        Assert.Contains("icacls", root.Problem);
        Assert.False(new DirectoryInfo(path).GetAccessControl().AreAccessRulesProtected);
    }

    [WindowsElevatedFact]
    public void A_held_work_folder_cannot_be_moved_aside_until_it_is_released()
    {
        var path = Path.Combine(_temp, "parent", "work");
        var root = WindowsWorkRoot.Open(path, allowUnprotected: false, mayChange: true);
        Assert.Null(root.Problem);

        Assert.ThrowsAny<IOException>(() => Directory.Move(path, Path.Combine(_temp, "parent", "moved")));
        Assert.ThrowsAny<IOException>(() => Directory.Move(Path.Combine(_temp, "parent"), Path.Combine(_temp, "moved")));

        root.Dispose();
        Directory.Move(path, Path.Combine(_temp, "parent", "moved"));
    }

    [WindowsElevatedFact]
    public void A_work_folder_opened_up_after_start_is_refused_at_the_next_check()
    {
        var path = Path.Combine(_temp, "work");
        using var root = WindowsWorkRoot.Open(path, allowUnprotected: false, mayChange: true);
        Assert.Null(root.Problem);

        var security = new DirectoryInfo(path).GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.Modify, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);

        // Owned by Administrators here, so refused; a folder the service owns is locked again instead.
        Assert.Contains("can be changed", root.Recheck());
    }

    [WindowsFact]
    public void Locking_a_folder_replaces_its_inherited_access_with_system_and_administrators_only()
    {
        // The reset an existing, service-made work folder gets; run here on a folder this account
        // owns, since only SYSTEM can make one SYSTEM owns.
        var path = Directory.CreateDirectory(Path.Combine(_temp, "work")).FullName;
        using (var handle = WindowsFileApi.TryOpen(path, WindowsFileApi.ReadAttributes | WindowsFileApi.ReadControl | WindowsFileApi.WriteDac,
                   WindowsFileApi.ShareReadWrite, out _)!)
        {
            WindowsFileApi.LockDown(handle);
            var (_, access) = WindowsFileApi.ReadAccess(handle);
            Assert.Empty(WorkAccess.UntrustedWriters(access!));
        }

        var security = new DirectoryInfo(path).GetAccessControl();
        try
        {
            Assert.True(security.AreAccessRulesProtected);
            Assert.All(security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(),
                rule => Assert.False(rule.IsInherited));
        }
        finally
        {
            // An unelevated owner keeps only the right to change access; give it back so the
            // folder can be cleaned up.
            security.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
            new DirectoryInfo(path).SetAccessControl(security);
        }
    }

    [WindowsFact]
    public void A_job_folder_is_created_only_where_nothing_was()
    {
        var job = Path.Combine(_temp, "job-5");

        Assert.Null(JobScratch.Create(job));
        Assert.True(Directory.Exists(job));
    }

    [WindowsFact]
    public void A_job_folder_planted_as_a_junction_is_refused_and_never_written_through()
    {
        var outside = Outside();
        var job = Path.Combine(_temp, "job-5");
        WindowsLinks.Junction(job, outside);

        Assert.Contains("link", JobScratch.Create(job));
        JobScratch.Delete(job);

        Assert.False(Directory.Exists(job));
        Assert.True(File.Exists(Path.Combine(outside, "precious")));
    }

    [WindowsFact]
    public void Removing_a_job_folder_removes_a_junction_inside_it_as_a_link()
    {
        var outside = Outside();
        var job = Path.Combine(_temp, "job-5");
        Assert.Null(JobScratch.Create(job));
        File.WriteAllText(Path.Combine(job, "source"), "scratch");
        Directory.CreateDirectory(Path.Combine(job, "logs"));
        File.WriteAllText(Path.Combine(job, "logs", "vmaf.json"), "{}");
        File.SetAttributes(Path.Combine(job, "source"), FileAttributes.ReadOnly);
        WindowsLinks.Junction(Path.Combine(job, "logs", "elsewhere"), outside);

        JobScratch.Delete(job);

        Assert.False(Directory.Exists(job));
        Assert.True(File.Exists(Path.Combine(outside, "precious")));
    }

    [WindowsElevatedFact]
    public void A_plain_job_folder_left_by_an_earlier_run_is_cleared_and_made_again()
    {
        // Given to Administrators, as one the service left would be owned by SYSTEM.
        var outside = Outside();
        var job = Directory.CreateDirectory(Path.Combine(_temp, "job-5")).FullName;
        var security = new DirectoryInfo(job).GetAccessControl();
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        new DirectoryInfo(job).SetAccessControl(security);
        File.WriteAllText(Path.Combine(job, "candidate.mkv"), "stale");
        WindowsLinks.Junction(Path.Combine(job, "elsewhere"), outside);

        Assert.Null(JobScratch.Create(job));

        Assert.Empty(Directory.EnumerateFileSystemEntries(job));
        Assert.True(File.Exists(Path.Combine(outside, "precious")));
    }

    private static void RemoveWithoutFollowing(DirectoryInfo directory)
    {
        if (!directory.Exists) return;
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            if (entry.LinkTarget is not null) entry.Delete();
            else if (entry is DirectoryInfo child) RemoveWithoutFollowing(child);
            else { entry.Attributes = FileAttributes.Normal; entry.Delete(); }
        }
        directory.Delete();
    }
}
