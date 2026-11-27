using System.Text.RegularExpressions;
namespace Optimisarr.Sidecar.Core.Session;

/// <summary>One entry of a Windows access list, reduced to what deciding trust needs.</summary>
/// <param name="Principal">The account or group, as a SID string.</param>
/// <param name="Rights">The access mask the entry grants or denies.</param>
public sealed record AccessEntry(string Principal, uint Rights, bool Allows = true);

/// <summary>
/// Who may change a file or folder a LocalSystem service acts on. Written over plain values,
/// because Windows' own access-control types do not load anywhere else and these rules must give
/// the same answer in the Linux test run.
/// </summary>
public static class WorkAccess
{
    public const string LocalSystem = "S-1-5-18";
    public const string Administrators = "S-1-5-32-544";
    public const string TrustedInstaller = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
    public const string Everyone = "S-1-1-0";

    /// <summary>
    /// Listing or reading, reading extended attributes and attributes, traversing or executing,
    /// reading the access list, waiting on the handle, and the generic read and execute rights.
    /// Everything else, including writing attributes, changes the object or who may change it.
    /// </summary>
    private const uint ReadAndExecute = 0x1 | 0x8 | 0x20 | 0x80 | 0x20000 | 0x100000 | 0x80000000 | 0x20000000;

    private static readonly HashSet<string> Trusted = new(StringComparer.OrdinalIgnoreCase)
    {
        LocalSystem, Administrators, TrustedInstaller,
    };

    /// <param name="self">The account this process runs as, when it should be trusted too: it can
    /// already do anything this process can. For the service it is SYSTEM, trusted anyway.</param>
    public static bool IsTrusted(string? principal, string? self = null) =>
        principal is not null && (Trusted.Contains(principal) || string.Equals(principal, self, StringComparison.OrdinalIgnoreCase));

    public static bool GrantsChange(uint rights) => (rights & ~ReadAndExecute) != 0;

    /// <summary>
    /// Every principal outside SYSTEM, Administrators and TrustedInstaller (and
    /// <paramref name="self"/>, when given) that an allow entry lets change the object. Entries that
    /// only apply to new children count too: on a folder they are how a later child becomes writable.
    /// </summary>
    public static IReadOnlyList<string> UntrustedWriters(IEnumerable<AccessEntry> access, string? self = null) =>
        access.Where(entry => entry.Allows && GrantsChange(entry.Rights) && !IsTrusted(entry.Principal, self))
            .Select(entry => entry.Principal)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>A missing access list is Windows' "no list": full access for everyone.</summary>
    public static IReadOnlyList<AccessEntry> OrEveryone(IReadOnlyList<AccessEntry>? access) => access ?? [new(Everyone, 0x1F01FF)];

    /// <summary>
    /// Why something already on disk must not be used as it stands, or null when it may. Anything
    /// the service opens again by name, rather than creating fresh, has to be owned and changeable
    /// only by accounts it trusts: otherwise its owner could swap what is inside for a link.
    /// </summary>
    public static string? AdoptionProblem(string path, string? owner, IReadOnlyList<AccessEntry>? access, string? self)
    {
        if (!IsTrusted(owner, self))
        {
            return $"{path} is owned by {owner ?? "an unknown account"}, not SYSTEM or Administrators, so it was not used.";
        }

        var writers = UntrustedWriters(OrEveryone(access), self);
        return writers.Count == 0
            ? null
            : $"{path} can be changed by accounts other than SYSTEM and Administrators ({string.Join(", ", writers)}), so it was not used.";
    }
}

/// <summary>One entry directly inside the work folder, read through a handle that did not follow it.</summary>
/// <param name="Owner">Read only for the service's own names; null for anything else.</param>
/// <param name="Access">Read only for the service's own names; null for anything else.</param>
public sealed record WorkRootEntry(string Name, bool IsLink, string? Owner, IReadOnlyList<AccessEntry>? Access);

/// <summary>What the work folder looks like, as read through handles that do not follow links.</summary>
/// <param name="LinkedComponent">The first folder on the path, the work folder included, that is
/// a junction, symbolic link or mounted folder; null when there is none.</param>
/// <param name="Access">The folder's access list; null when it has none, which Windows reads as
/// full access for everyone.</param>
/// <param name="Entries">What is directly inside it; null when that was not read, as on the cheap
/// recheck before each job, which is then never enough to rebuild the folder.</param>
public sealed record WorkRootFacts(
    bool Exists,
    string? LinkedComponent = null,
    bool VolumeHasAccessControl = true,
    string? Owner = null,
    IReadOnlyList<AccessEntry>? Access = null,
    IReadOnlyList<WorkRootEntry>? Entries = null);

public enum WorkRootAction
{
    /// <summary>The folder is sound as it stands.</summary>
    Use,

    /// <summary>The folder is missing; create it with administrator-only access.</summary>
    Create,

    /// <summary>
    /// Other accounts can change it or the service's leftovers in it, and it holds nothing else:
    /// delete those leftovers and the folder, and create it again administrator-only.
    /// </summary>
    Rebuild,

    /// <summary>Take no work there, for the reason given.</summary>
    Refuse,
}

/// <param name="Notice">Something an administrator should hear about even though work goes on.</param>
public sealed record WorkRootVerdict(WorkRootAction Action, string? Problem = null, string? Notice = null);

/// <summary>
/// Whether a LocalSystem service may work in a folder.
///
/// <para>Windows lets every signed-in account create folders at the root of a drive, and gives them
/// "Modify" on anything beneath that inherits the drive's access. A work folder left like that lets
/// any of them delete it and put a junction in its place, or plant <c>job-&lt;id&gt;</c> as a junction
/// before the job arrives, and the service would then create and delete files wherever the junction
/// points, with SYSTEM's rights. So the folder must be owned by SYSTEM or Administrators, changeable
/// by no one else, and reached without links.</para>
///
/// <para>Every work folder the service created before this check has the drive's inherited access,
/// and so does everything inside it. Resetting only the folder's own access would not be enough:
/// what is inside keeps its access, and a handle another account opened before the reset keeps
/// working. Such a folder is therefore rebuilt, deleted with the service's leftovers and created
/// again, when it holds nothing but those leftovers. It is owned by SYSTEM or by Administrators,
/// whichever the machine gives a service's new folders. Anything else inside means somebody keeps
/// their own files there, and the folder is refused and left exactly as it is.</para>
/// </summary>
public static partial class WorkRootPolicy
{
    /// <summary>
    /// A file the service keeps in the work folder and holds open while it runs. An empty folder
    /// can be turned into a junction in place by anyone holding a handle to it that allows writing
    /// its attributes, and a held file cannot be deleted, so the folder is never empty.
    /// </summary>
    public const string SentinelName = ".optimisarr-work-folder";

    /// <summary>
    /// Whether a name in the work folder is one the service itself creates there: the job folders
    /// <c>job-&lt;id&gt;</c> and <c>job-&lt;id&gt;-&lt;lease&gt;-&lt;run&gt;</c>, the native workspaces
    /// <c>native-job-&lt;id&gt;-&lt;lease&gt;-&lt;owner&gt;</c> and <c>native-capability-&lt;owner&gt;</c>,
    /// and its control folder and sentinel. A rebuild deletes these, so the match is exact: a
    /// folder of someone's own that merely starts with "job-" must never be taken for one.
    /// </summary>
    public static bool IsServiceEntry(string name) =>
        string.Equals(name, NativeScratchWorkspace.ControlDirectoryName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, SentinelName, StringComparison.OrdinalIgnoreCase)
        || ServiceEntryName().IsMatch(name);

    [GeneratedRegex(@"\A(?:job-[0-9]+(?:-[0-9a-f]{32}-[0-9a-f]{32})?|native-job-[0-9]+-[0-9a-f]{32}-[0-9a-f]{32}|native-capability-[0-9a-f]{32})\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ServiceEntryName();

    public static WorkRootVerdict Decide(string root, WorkRootFacts facts, bool allowUnprotected)
    {
        if (facts.LinkedComponent is { } link)
        {
            return Refuse($"{link} is a link (a junction, symbolic link or mounted folder), so the files written beneath it could be redirected. "
                + "Use a folder reached without links, on the drive's own letter.");
        }

        if (!facts.VolumeHasAccessControl)
        {
            if (!allowUnprotected)
            {
                return Refuse($"{root} is on a drive without access control (such as FAT32 or exFAT, which many RAM drives use), "
                    + "so any account on this machine could change or replace a job's files there, including the VMAF log its quality is judged by. "
                    + $"Format the drive as NTFS, or accept the risk by setting the machine-wide variable {WorkLocation.AllowUnprotectedVariable}=1 and restarting the service.");
            }

            return new(facts.Exists ? WorkRootAction.Use : WorkRootAction.Create,
                Notice: $"{root} is on a drive without access control, used because {WorkLocation.AllowUnprotectedVariable} is set: "
                    + "any account on this machine can change or replace a job's files there, including the VMAF log its quality is judged by.");
        }

        if (!facts.Exists)
        {
            return new(WorkRootAction.Create);
        }

        if (!WorkAccess.IsTrusted(facts.Owner))
        {
            return Refuse($"{root} is owned by {facts.Owner ?? "an unknown account"}, not SYSTEM or Administrators, so it was not trusted. {Fix(root)}");
        }

        var writers = WorkAccess.UntrustedWriters(WorkAccess.OrEveryone(facts.Access));
        var tainted = (facts.Entries ?? []).Where(entry => IsServiceEntry(entry.Name) && Tainted(entry)).Select(entry => entry.Name).ToArray();
        if (writers.Count == 0 && tainted.Length == 0)
        {
            return new(WorkRootAction.Use);
        }

        var reason = writers.Count > 0
            ? $"can be changed by accounts other than SYSTEM and Administrators ({string.Join(", ", writers)})"
            : $"holds {Names(tainted)}, which accounts other than SYSTEM and Administrators can change or which are links";
        if (facts.Entries is null)
        {
            return Refuse($"{root} {reason}, so it was not used. {Fix(root)}");
        }

        var foreign = facts.Entries.Where(entry => !IsServiceEntry(entry.Name)).Select(entry => entry.Name).ToArray();
        if (foreign.Length > 0)
        {
            return Refuse($"{root} {reason}, and it holds files the worker did not make ({Names(foreign)}), so it was not used and nothing in it was changed. "
                + "Move them out of it and restart the service, which then deletes the folder and creates it again for SYSTEM and Administrators only.");
        }

        return new(WorkRootAction.Rebuild,
            Notice: $"{root} {reason}. It held only the worker's own leftovers, so they and the folder were deleted, "
                + "and it was created again for SYSTEM and Administrators only.");
    }

    private static bool Tainted(WorkRootEntry entry) =>
        entry.IsLink || !WorkAccess.IsTrusted(entry.Owner) || WorkAccess.UntrustedWriters(WorkAccess.OrEveryone(entry.Access)).Count > 0;

    private static string Names(IReadOnlyList<string> names) =>
        string.Join(", ", names.Take(3)) + (names.Count > 3 ? $" and {names.Count - 3} more" : "");

    private static WorkRootVerdict Refuse(string problem) => new(WorkRootAction.Refuse, problem);

    private static string Fix(string root) =>
        $"From an administrator command prompt, run: icacls \"{root}\" /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F "
        + $"then: icacls \"{root}\" /setowner *S-1-5-32-544 and restart the service. Files already inside keep their own access, "
        + "so if it holds nothing you need, delete the folder instead and let the service create it.";
}

/// <summary>What is already at a job's folder path when the service goes to create it.</summary>
/// <param name="Access">Its access list; null when it has none, which Windows reads as full access
/// for everyone.</param>
public sealed record JobFolderFacts(bool IsLink, bool IsDirectory, string? Owner, bool VolumeHasAccessControl = true,
    IReadOnlyList<AccessEntry>? Access = null);

public enum JobFolderAction { Create, ClearThenCreate, Refuse }

public sealed record JobFolderDecision(JobFolderAction Action, string? Problem = null);

/// <summary>
/// A job's folder is created fresh, never adopted. Job ids are predictable, so anything already at
/// <c>job-&lt;id&gt;</c> could have been put there to be written through. Only a plain folder an
/// earlier run of the service left behind, that no other account can change, is cleared away;
/// anything else stops the job. Clearing walks into the folder, and anything another account can
/// change could be emptied and turned into a link while that walk is under way.
/// </summary>
public static class JobFolderPolicy
{
    /// <param name="self">The account this process runs as: see <see cref="WorkAccess.IsTrusted"/>.</param>
    public static JobFolderDecision Decide(string path, JobFolderFacts? existing, string? self = null)
    {
        if (existing is null)
        {
            return new(JobFolderAction.Create);
        }

        if (existing.IsLink)
        {
            return new(JobFolderAction.Refuse,
                $"{path} already exists as a link, so the job was not started there. Delete the link from an administrator prompt.");
        }

        if (!existing.IsDirectory)
        {
            return new(JobFolderAction.Refuse, $"{path} already exists as a file, so the job was not started there. Delete it from an administrator prompt.");
        }

        if (!existing.VolumeHasAccessControl)
        {
            return new(JobFolderAction.ClearThenCreate);
        }

        if (!WorkAccess.IsTrusted(existing.Owner, self))
        {
            return new(JobFolderAction.Refuse,
                $"{path} was made by another account, so the job was not started there. Delete it from an administrator prompt.");
        }

        var writers = WorkAccess.UntrustedWriters(WorkAccess.OrEveryone(existing.Access), self);
        return writers.Count == 0
            ? new(JobFolderAction.ClearThenCreate)
            : new(JobFolderAction.Refuse,
                $"{path} can be changed by accounts other than SYSTEM and Administrators ({string.Join(", ", writers)}), "
                + "so it was not cleared and the job was not started there. Delete it from an administrator prompt.");
    }
}
