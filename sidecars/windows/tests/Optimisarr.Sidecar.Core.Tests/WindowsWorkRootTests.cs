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

    [WindowsFact]
    public void An_owner_is_named_by_account_when_windows_knows_it_and_left_unnamed_when_it_does_not()
    {
        // The name is localised, so only its DOMAIN\name shape is checked.
        var system = WindowsFileApi.AccountName(WorkAccess.LocalSystem);
        Assert.NotNull(system);
        Assert.Contains('\\', system);
        Assert.Null(WindowsFileApi.AccountName("S-1-5-21-1-2-3-999999"));
        Assert.Null(WindowsFileApi.AccountName("not a security id"));
        Assert.Null(WindowsFileApi.AccountName(null));
    }

    [WindowsElevatedFact]
    public void A_missing_work_folder_is_created_for_system_and_administrators_only()
    {
        var path = Path.Combine(_temp, "work");

        using var root = WindowsWorkRoot.Open(path, allowUnprotected: false, mayChange: true);

        Assert.Null(root.Problem);
        AssertLockedToSystemAndAdministrators(path);
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
    public void A_work_folder_holding_files_the_service_did_not_make_is_refused_and_left_as_it_is()
    {
        // Somebody keeps their own files there: not the service's folder to delete or rewrite.
        var path = Directory.CreateDirectory(Path.Combine(_temp, "work")).FullName;
        GiveToAdministratorsAndOpenToUsers(path);
        File.WriteAllText(Path.Combine(path, "holiday.mkv"), "theirs");
        Directory.CreateDirectory(Path.Combine(path, "job-1"));

        using var root = WindowsWorkRoot.Open(path, allowUnprotected: false, mayChange: true);

        Assert.Contains("holiday.mkv", root.Problem);
        Assert.True(File.Exists(Path.Combine(path, "holiday.mkv")));
        Assert.True(Directory.Exists(Path.Combine(path, "job-1")));
        Assert.False(new DirectoryInfo(path).GetAccessControl().AreAccessRulesProtected);
    }

    [WindowsElevatedFact]
    public void A_work_folder_left_open_to_other_accounts_is_rebuilt_fresh_and_its_old_contents_and_handles_retired()
    {
        // What a folder an earlier version of the service made looks like: owned by Administrators
        // (or SYSTEM), with the drive's inherited access on it and on everything inside.
        var path = Directory.CreateDirectory(Path.Combine(_temp, "work")).FullName;
        GiveToAdministratorsAndOpenToUsers(path);
        Directory.CreateDirectory(Path.Combine(path, ".optimisarr-native"));
        File.WriteAllText(Path.Combine(path, ".optimisarr-native", "root.lock"), "");
        Directory.CreateDirectory(Path.Combine(path, "job-1"));
        File.WriteAllText(Path.Combine(path, "job-1", "candidate.mkv"), "stale");
        // A handle another account opened before; after an in-place reset it would still work.
        using var earlier = WindowsFileApi.TryOpen(path, WindowsFileApi.ReadAttributes, WindowsFileApi.ShareAll, out _)!;
        var before = WindowsFileApi.Identify(earlier);
        var notices = new List<string>();

        using var root = WindowsWorkRoot.Open(path, allowUnprotected: false, mayChange: true, notices.Add);

        Assert.Null(root.Problem);
        Assert.Contains(notices, notice => notice.Contains("created again"));
        Assert.Equal([WorkRootPolicy.SentinelName], Directory.EnumerateFileSystemEntries(path).Select(Path.GetFileName));
        using (var now = WindowsFileApi.TryOpen(path, WindowsFileApi.ReadAttributes, WindowsFileApi.ShareAll, out _)!)
        {
            Assert.False(WindowsFileApi.Identify(now).SameObject(before));
        }
        AssertLockedToSystemAndAdministrators(path);
    }

    [WindowsElevatedFact]
    public void The_service_holds_a_file_in_the_work_folder_so_it_is_never_empty()
    {
        // Only an empty folder can be turned into a junction in place.
        var path = Path.Combine(_temp, "work");
        var sentinel = Path.Combine(path, WorkRootPolicy.SentinelName);
        var root = WindowsWorkRoot.Open(path, allowUnprotected: false, mayChange: true);
        Assert.Null(root.Problem);

        Assert.True(File.Exists(sentinel));
        Assert.ThrowsAny<IOException>(() => File.Delete(sentinel));

        root.Dispose();
        File.Delete(sentinel);
    }

    [WindowsFact]
    public void A_pairing_prompt_neither_creates_the_work_folder_nor_calls_its_absence_a_fault()
    {
        var path = Path.Combine(_temp, "work");

        using var root = WindowsWorkRoot.Open(path, allowUnprotected: false, mayChange: false);

        Assert.Contains("will be created when the service starts", root.Problem);
        Assert.False(Directory.Exists(path));
    }

    [WindowsElevatedFact]
    public void A_folder_on_the_path_turned_into_a_junction_while_it_is_checked_is_refused_and_nothing_is_made_at_its_target()
    {
        var outside = Outside();
        var parent = Directory.CreateDirectory(Path.Combine(_temp, "parent")).FullName;
        var path = Path.Combine(parent, "work");

        // Still empty once it is open, so another account may convert it before the work folder is looked up.
        using (var root = WindowsWorkRoot.Open(path, allowUnprotected: false, mayChange: true, notice: null, afterOpening: opened =>
               {
                   if (string.Equals(opened, parent, StringComparison.OrdinalIgnoreCase)) WindowsLinks.ConvertToJunction(parent, outside);
               }))
        {
            Assert.NotNull(root.Problem);
        }

        Assert.False(Directory.Exists(Path.Combine(outside, "work")));
        Assert.True(File.Exists(Path.Combine(outside, "precious")));
        EmptyWithoutFollowing(parent);
    }

    [WindowsFact]
    public void A_folder_emptied_and_turned_into_a_junction_while_it_is_being_removed_is_never_followed()
    {
        var outside = Outside();
        var job = Path.Combine(_temp, "job-5");
        var logs = Directory.CreateDirectory(Path.Combine(job, "logs")).FullName;
        File.WriteAllText(Path.Combine(logs, "vmaf.json"), "{}");
        using var folder = WindowsScratchEntry.TryOpen(job)!;

        // A POSIX-style delete takes the name away when the deleting handle closes, as the walk
        // closes each child before removing its parent, so the junction is checked after that.
        using (var held = folder.Open("logs"))
        {
            // Holding it does not stop another account emptying it and converting it in place.
            File.Delete(Path.Combine(logs, "vmaf.json"));
            WindowsLinks.ConvertToJunction(logs, outside);

            Assert.Empty(held.Names());
            ScratchTree.Delete(held);
        }

        Assert.True(File.Exists(Path.Combine(outside, "precious")));
        Assert.False(Directory.Exists(logs));
    }

    [WindowsFact]
    public void A_leftover_job_folder_other_accounts_can_change_is_refused_and_not_cleared()
    {
        var job = Directory.CreateDirectory(Path.Combine(_temp, "job-5")).FullName;
        File.WriteAllText(Path.Combine(job, "candidate.mkv"), "stale");
        OpenToUsers(job);

        Assert.Contains("can be changed", JobScratch.Create(job));
        Assert.True(File.Exists(Path.Combine(job, "candidate.mkv")));
    }

    [WindowsFact]
    public void A_native_control_folder_other_accounts_can_change_is_refused_before_it_is_used()
    {
        var root = Directory.CreateDirectory(Path.Combine(_temp, "native-root")).FullName;
        var control = Directory.CreateDirectory(Path.Combine(root, NativeScratchWorkspace.ControlDirectoryName)).FullName;
        OpenToUsers(control);

        Assert.ThrowsAny<IOException>(() => NativeScratchWorkspace.CreateCapability(root));
        Assert.Empty(Directory.EnumerateFileSystemEntries(control));
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

        OpenToUsers(path);

        // Never rebuilt under jobs that may be running: refused until the service restarts.
        Assert.Contains("Restart the service", root.Recheck());
        Assert.True(File.Exists(Path.Combine(path, WorkRootPolicy.SentinelName)));
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

    private static void OpenToUsers(string path)
    {
        var security = new DirectoryInfo(path).GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.Modify, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }

    private static void GiveToAdministratorsAndOpenToUsers(string path)
    {
        OpenToUsers(path);
        var security = new DirectoryInfo(path).GetAccessControl();
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        new DirectoryInfo(path).SetAccessControl(security);
    }

    private static void AssertLockedToSystemAndAdministrators(string path)
    {
        var security = new DirectoryInfo(path).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        var principals = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Select(rule => rule.IdentityReference.Value).Distinct().Order().ToArray();
        Assert.Equal(new[] { WorkAccess.LocalSystem, WorkAccess.Administrators }.Order().ToArray(), principals);
        Assert.True(WorkAccess.IsTrusted(security.GetOwner(typeof(SecurityIdentifier))?.Value));
    }

    /// <summary>Removes what was made inside a folder that is now a junction, without following it.</summary>
    private static void EmptyWithoutFollowing(string folder)
    {
        using var held = WindowsScratchEntry.TryOpen(folder)!;
        using var listing = WindowsFileApi.TryOpen(folder, WindowsFileApi.ListDirectory, WindowsFileApi.ShareAll, out _)!;
        foreach (var name in WindowsFileApi.ListNames(listing))
        {
            using var child = held.Open(name);
            ScratchTree.Delete(child);
        }
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
