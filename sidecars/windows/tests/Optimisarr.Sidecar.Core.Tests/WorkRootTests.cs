using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Core.Tests;

/// <summary>
/// The work folder decides where a LocalSystem service writes and deletes, so only SYSTEM and
/// Administrators may change it. These are the decisions, over a described folder, that the Windows
/// code acts on; the real access lists are exercised in <see cref="WindowsWorkRootTests"/>.
/// </summary>
public sealed class WorkRootTests
{
    private const string Root = @"D:\OptimisarrWork";
    private const string System = WorkAccess.LocalSystem;
    private const string Administrators = WorkAccess.Administrators;
    private const string AuthenticatedUsers = "S-1-5-11";
    private const string Users = "S-1-5-32-545";
    private const uint FullControl = 0x1F01FF;
    private const uint Modify = 0x1301BF;
    private const uint ReadAndExecute = 0x1200A9;

    private static readonly AccessEntry[] AdminOnly = [new(System, FullControl), new(Administrators, FullControl)];

    private static WorkRootEntry Entry(string name, bool writable = false) =>
        new(name, IsLink: false, Owner: System, Access: writable ? [.. AdminOnly, new(Users, Modify)] : AdminOnly);

    [Fact]
    public void A_missing_work_folder_is_created()
    {
        Assert.Equal(WorkRootAction.Create, WorkRootPolicy.Decide(Root, new(Exists: false), allowUnprotected: false).Action);
    }

    [Fact]
    public void A_folder_only_system_and_administrators_can_change_is_used()
    {
        var verdict = WorkRootPolicy.Decide(Root, new(true, Owner: Administrators, Access: [.. AdminOnly, new(Users, ReadAndExecute)]), false);

        Assert.Equal(WorkRootAction.Use, verdict.Action);
        Assert.Null(verdict.Problem);
    }

    [Fact]
    public void A_folder_the_service_made_that_other_accounts_can_change_is_rebuilt()
    {
        // What every existing C:\OptimisarrWork looks like: created by the service, so owned by
        // SYSTEM, but carrying the drive root's inherited "Authenticated Users: Modify", and
        // holding only what the service itself puts there.
        var verdict = WorkRootPolicy.Decide(Root, new(true, Owner: System, Access: [.. AdminOnly, new(AuthenticatedUsers, Modify)],
            Entries: [Entry(".optimisarr-native", writable: true), Entry("job-1", writable: true), Entry("native-job-1-0123456789abcdef0123456789abcdef-fedcba9876543210fedcba9876543210", writable: true)]), false);

        Assert.Equal(WorkRootAction.Rebuild, verdict.Action);
        Assert.Contains("created again", verdict.Notice);
        Assert.Contains(AuthenticatedUsers, verdict.Notice);
    }

    [Fact]
    public void An_empty_folder_the_service_made_that_other_accounts_can_change_is_rebuilt()
    {
        var verdict = WorkRootPolicy.Decide(Root, new(true, Owner: System, Access: [.. AdminOnly, new(Users, Modify)], Entries: []), false);

        Assert.Equal(WorkRootAction.Rebuild, verdict.Action);
    }

    [Fact]
    public void A_folder_administrators_own_that_holds_only_the_services_files_is_rebuilt()
    {
        // A service running as SYSTEM can be given Administrators as the default owner of what it
        // creates, so an Administrators-owned folder is as likely to be the service's own.
        var verdict = WorkRootPolicy.Decide(Root, new(true, Owner: Administrators, Access: [.. AdminOnly, new(Users, Modify)],
            Entries: [Entry("job-7", writable: true)]), false);

        Assert.Equal(WorkRootAction.Rebuild, verdict.Action);
    }

    [Fact]
    public void A_changeable_folder_holding_files_the_service_did_not_make_is_refused_and_left_alone()
    {
        var verdict = WorkRootPolicy.Decide(Root, new(true, Owner: Administrators, Access: [.. AdminOnly, new(Users, Modify)],
            Entries: [Entry("job-7", writable: true), Entry("holiday.mkv", writable: true)]), false);

        Assert.Equal(WorkRootAction.Refuse, verdict.Action);
        Assert.Contains("holiday.mkv", verdict.Problem);
        Assert.Contains("did not make", verdict.Problem);
        Assert.DoesNotContain("job-7", verdict.Problem);
    }

    [Fact]
    public void A_locked_folder_whose_leftovers_other_accounts_can_change_is_rebuilt()
    {
        // Locked in place by an earlier build: the folder is sound, but what was already inside
        // kept the drive's access, and a handle opened before the reset still works.
        var verdict = WorkRootPolicy.Decide(Root, new(true, Owner: System, Access: AdminOnly,
            Entries: [Entry(".optimisarr-native", writable: true), Entry(WorkRootPolicy.SentinelName)]), false);

        Assert.Equal(WorkRootAction.Rebuild, verdict.Action);
        Assert.Contains(".optimisarr-native", verdict.Notice);
    }

    [Fact]
    public void A_locked_folder_with_a_job_folder_planted_as_a_link_is_rebuilt()
    {
        var verdict = WorkRootPolicy.Decide(Root, new(true, Owner: System, Access: AdminOnly,
            Entries: [new WorkRootEntry("job-3", IsLink: true, Owner: System, Access: AdminOnly)]), false);

        Assert.Equal(WorkRootAction.Rebuild, verdict.Action);
    }

    [Fact]
    public void A_locked_folder_holding_other_files_is_used_whatever_their_access()
    {
        // The service never opens them, so who can change them is not its concern.
        var verdict = WorkRootPolicy.Decide(Root, new(true, Owner: System, Access: AdminOnly,
            Entries: [Entry("job-1"), new WorkRootEntry("notes.txt", false, null, null)]), false);

        Assert.Equal(WorkRootAction.Use, verdict.Action);
    }

    [Fact]
    public void A_locked_folder_with_changeable_leftovers_beside_files_the_service_did_not_make_is_refused()
    {
        var verdict = WorkRootPolicy.Decide(Root, new(true, Owner: System, Access: AdminOnly,
            Entries: [Entry("job-1", writable: true), new WorkRootEntry("notes.txt", false, null, null)]), false);

        Assert.Equal(WorkRootAction.Refuse, verdict.Action);
        Assert.Contains("notes.txt", verdict.Problem);
    }

    [Fact]
    public void A_changeable_folder_holding_someones_own_job_folder_is_refused_not_rebuilt()
    {
        var verdict = WorkRootPolicy.Decide(Root, new(true, Owner: System, Access: [.. AdminOnly, new(Users, Modify)],
            Entries: [Entry("job-applications", writable: true), Entry("native-instruments", writable: true)]), false);

        Assert.Equal(WorkRootAction.Refuse, verdict.Action);
        Assert.Contains("job-applications", verdict.Problem);
    }

    [Fact]
    public void A_changeable_folder_whose_contents_were_not_read_is_refused_not_rebuilt()
    {
        var verdict = WorkRootPolicy.Decide(Root, new(true, Owner: System, Access: [.. AdminOnly, new(Users, Modify)]), false);

        Assert.Equal(WorkRootAction.Refuse, verdict.Action);
        Assert.Contains("can be changed", verdict.Problem);
    }

    [Theory]
    [InlineData(".optimisarr-native", true)]
    [InlineData(".optimisarr-work-folder", true)]
    [InlineData("job-5", true)]
    [InlineData("job-5-0123456789abcdef0123456789abcdef-0123456789abcdef0123456789abcdef", true)]
    [InlineData("JOB-5", true)]
    [InlineData("native-job-5-0123456789abcdef0123456789abcdef-0123456789abcdef0123456789abcdef", true)]
    [InlineData("native-capability-0123456789abcdef0123456789abcdef", true)]
    [InlineData("job-", false)]
    [InlineData("job-applications", false)]
    [InlineData("job-5-cv", false)]
    [InlineData("job-5-0123abcd-4567", false)]
    [InlineData("native-instruments", false)]
    [InlineData("native-job-5-a-b", false)]
    [InlineData("native-capability-a", false)]
    [InlineData("job-5\n", false)]
    [InlineData("native-", false)]
    [InlineData("jobs", false)]
    [InlineData("holiday.mkv", false)]
    [InlineData(".optimisarr-native-old", false)]
    public void Only_the_names_the_service_itself_creates_count_as_its_own(string name, bool own)
    {
        Assert.Equal(own, WorkRootPolicy.IsServiceEntry(name));
    }

    [Fact]
    public void A_folder_someone_else_owns_is_refused_with_the_fix()
    {
        var verdict = WorkRootPolicy.Decide(Root, new(true, Owner: "S-1-5-21-1-2-3-1001", Access: AdminOnly), false);

        Assert.Equal(WorkRootAction.Refuse, verdict.Action);
        Assert.Contains("owned by", verdict.Problem);
        Assert.Contains("icacls", verdict.Problem);
        Assert.Contains(Root, verdict.Problem);
    }

    [Fact]
    public void A_folder_with_no_access_list_at_all_is_open_to_everyone()
    {
        var verdict = WorkRootPolicy.Decide(Root, new(true, Owner: Administrators, Access: null), false);

        Assert.Equal(WorkRootAction.Refuse, verdict.Action);
        Assert.Contains(WorkAccess.Everyone, verdict.Problem);
    }

    [Fact]
    public void A_folder_reached_through_a_link_is_refused()
    {
        var verdict = WorkRootPolicy.Decide(Root, new(true, LinkedComponent: @"D:\data", Owner: System, Access: AdminOnly), false);

        Assert.Equal(WorkRootAction.Refuse, verdict.Action);
        Assert.Contains(@"D:\data", verdict.Problem);
        Assert.Contains("link", verdict.Problem);
    }

    [Fact]
    public void A_drive_without_access_control_is_refused_unless_the_risk_is_accepted()
    {
        var refused = WorkRootPolicy.Decide(Root, new(true, VolumeHasAccessControl: false), allowUnprotected: false);
        var accepted = WorkRootPolicy.Decide(Root, new(true, VolumeHasAccessControl: false), allowUnprotected: true);
        var created = WorkRootPolicy.Decide(Root, new(false, VolumeHasAccessControl: false), allowUnprotected: true);

        Assert.Equal(WorkRootAction.Refuse, refused.Action);
        Assert.Contains(WorkLocation.AllowUnprotectedVariable, refused.Problem);
        Assert.Contains("FAT", refused.Problem);
        // Nothing can be locked on such a drive, so the risk is to the job's files themselves.
        Assert.Contains("VMAF", refused.Problem);
        Assert.DoesNotContain("redirect", refused.Problem);
        Assert.Equal(WorkRootAction.Use, accepted.Action);
        Assert.Contains("any account", accepted.Notice);
        Assert.DoesNotContain("redirect", accepted.Notice);
        Assert.Equal(WorkRootAction.Create, created.Action);
    }

    [Fact]
    public void Accepting_an_unprotected_drive_does_not_excuse_a_link()
    {
        var verdict = WorkRootPolicy.Decide(Root, new(true, LinkedComponent: Root, VolumeHasAccessControl: false), allowUnprotected: true);

        Assert.Equal(WorkRootAction.Refuse, verdict.Action);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("yes", false)]
    [InlineData("1", true)]
    [InlineData(" 1 ", true)]
    public void Only_an_explicit_one_accepts_an_unprotected_drive(string? value, bool allowed)
    {
        Assert.Equal(allowed, WorkLocation.AllowsUnprotected(value));
    }

    [Theory]
    [InlineData(0x1u)]          // list folder / read data
    [InlineData(0x1200A9u)]     // read and execute
    [InlineData(0x80000000u)]   // generic read
    [InlineData(0x20000000u)]   // generic execute
    public void Reading_and_traversing_grants_no_change(uint rights)
    {
        Assert.False(WorkAccess.GrantsChange(rights));
    }

    [Theory]
    [InlineData(0x2u)]          // write data / add file
    [InlineData(0x4u)]          // append data / add subfolder
    [InlineData(0x10u)]         // write extended attributes
    [InlineData(0x40u)]         // delete child
    [InlineData(0x100u)]        // write attributes
    [InlineData(0x10000u)]      // delete
    [InlineData(0x40000u)]      // change permissions
    [InlineData(0x80000u)]      // take ownership
    [InlineData(0x1000000u)]    // access system security
    [InlineData(0x40000000u)]   // generic write
    [InlineData(0x10000000u)]   // generic all
    [InlineData(0x1301BFu)]     // modify
    public void Anything_beyond_reading_and_traversing_is_a_change(uint rights)
    {
        Assert.True(WorkAccess.GrantsChange(rights));
    }

    [Fact]
    public void A_deny_entry_never_makes_anyone_a_writer()
    {
        Assert.Empty(WorkAccess.UntrustedWriters([new(Users, FullControl, Allows: false)]));
        Assert.Equal([Users], WorkAccess.UntrustedWriters([new(Users, 0x100), new(Users, 0x2)]));
    }

    [Fact]
    public void A_missing_job_folder_is_simply_created()
    {
        Assert.Equal(JobFolderAction.Create, JobFolderPolicy.Decide(@"D:\w\job-5", existing: null).Action);
    }

    [Fact]
    public void A_job_folder_left_by_an_earlier_run_of_the_service_is_cleared()
    {
        Assert.Equal(JobFolderAction.ClearThenCreate,
            JobFolderPolicy.Decide(@"D:\w\job-5", new(IsLink: false, IsDirectory: true, Owner: System, Access: AdminOnly)).Action);
    }

    [Fact]
    public void A_leftover_job_folder_other_accounts_can_change_is_refused_not_cleared()
    {
        // Clearing walks into it, and anything another account can change could be turned into a
        // link while the walk is under way.
        var decision = JobFolderPolicy.Decide(@"D:\w\job-5", new(false, true, System, Access: [.. AdminOnly, new(Users, Modify)]));

        Assert.Equal(JobFolderAction.Refuse, decision.Action);
        Assert.Contains("can be changed", decision.Problem);
        Assert.Contains(Users, decision.Problem);
    }

    [Fact]
    public void A_leftover_job_folder_with_no_access_list_is_refused()
    {
        Assert.Equal(JobFolderAction.Refuse, JobFolderPolicy.Decide(@"D:\w\job-5", new(false, true, System, Access: null)).Action);
    }

    [Fact]
    public void A_leftover_job_folder_only_this_account_can_change_is_cleared()
    {
        // The account the worker runs as can already do anything the worker can.
        const string self = "S-1-5-21-1-2-3-1001";
        var decision = JobFolderPolicy.Decide(@"D:\w\job-5", new(false, true, self, Access: [.. AdminOnly, new(self, FullControl)]), self);

        Assert.Equal(JobFolderAction.ClearThenCreate, decision.Action);
    }

    [Fact]
    public void A_job_folder_planted_as_a_link_is_refused_and_never_followed()
    {
        var decision = JobFolderPolicy.Decide(@"D:\w\job-5", new(IsLink: true, IsDirectory: true, Owner: System));

        Assert.Equal(JobFolderAction.Refuse, decision.Action);
        Assert.Contains("link", decision.Problem);
        Assert.Contains("job-5", decision.Problem);
    }

    [Fact]
    public void A_job_folder_another_account_made_is_refused()
    {
        var decision = JobFolderPolicy.Decide(@"D:\w\job-5", new(false, true, "S-1-5-21-1-2-3-1001"));

        Assert.Equal(JobFolderAction.Refuse, decision.Action);
        Assert.Contains("another account", decision.Problem);
    }

    [Fact]
    public void A_file_where_the_job_folder_belongs_is_refused()
    {
        Assert.Equal(JobFolderAction.Refuse, JobFolderPolicy.Decide(@"D:\w\job-5", new(false, IsDirectory: false, System)).Action);
    }

    [Fact]
    public void On_a_drive_without_owners_a_plain_leftover_folder_is_cleared()
    {
        // Only reachable once the administrator has accepted an unprotected drive.
        Assert.Equal(JobFolderAction.ClearThenCreate,
            JobFolderPolicy.Decide(@"R:\w\job-5", new(false, true, Owner: null, VolumeHasAccessControl: false)).Action);
    }

    [Fact]
    public void Something_the_service_adopts_must_be_owned_and_changeable_only_by_trusted_accounts()
    {
        const string self = "S-1-5-21-1-2-3-1001";

        Assert.Null(WorkAccess.AdoptionProblem(@"D:\w\.optimisarr-native", System, AdminOnly, self: null));
        Assert.Null(WorkAccess.AdoptionProblem(@"D:\w\.optimisarr-native", self, [.. AdminOnly, new(self, FullControl)], self));
        Assert.Contains("owned by", WorkAccess.AdoptionProblem(@"D:\w\.optimisarr-native", "S-1-5-21-9", AdminOnly, self));
        Assert.Contains(Users, WorkAccess.AdoptionProblem(@"D:\w\.optimisarr-native", System, [.. AdminOnly, new(Users, Modify)], self));
        Assert.Contains(WorkAccess.Everyone, WorkAccess.AdoptionProblem(@"D:\w\.optimisarr-native", System, null, self));
    }
}
