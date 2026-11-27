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

    public static bool IsTrusted(string? principal) => principal is not null && Trusted.Contains(principal);

    public static bool GrantsChange(uint rights) => (rights & ~ReadAndExecute) != 0;

    /// <summary>
    /// Every principal outside SYSTEM, Administrators and TrustedInstaller that an allow entry
    /// lets change the object. Entries that only apply to new children count too: on a folder they
    /// are how a later child becomes writable.
    /// </summary>
    public static IReadOnlyList<string> UntrustedWriters(IEnumerable<AccessEntry> access) =>
        access.Where(entry => entry.Allows && GrantsChange(entry.Rights) && !IsTrusted(entry.Principal))
            .Select(entry => entry.Principal)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}

/// <summary>What the work folder looks like, as read through handles that do not follow links.</summary>
/// <param name="LinkedComponent">The first folder on the path, the work folder included, that is
/// a junction, symbolic link or mounted folder; null when there is none.</param>
/// <param name="Access">The folder's access list; null when it has none, which Windows reads as
/// full access for everyone.</param>
public sealed record WorkRootFacts(
    bool Exists,
    string? LinkedComponent = null,
    bool VolumeHasAccessControl = true,
    string? Owner = null,
    IReadOnlyList<AccessEntry>? Access = null);

public enum WorkRootAction
{
    /// <summary>The folder is sound as it stands.</summary>
    Use,

    /// <summary>The folder is missing; create it with administrator-only access.</summary>
    Create,

    /// <summary>The service made it earlier, before it was locked: reset it to administrator-only.</summary>
    LockDown,

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
/// <para>Every work folder the service created before this check has the drive's inherited access
/// and SYSTEM as its owner. Those are reset in place, because the service made them and nobody else
/// chose their access. A folder anyone else owns is refused with the commands that fix it: changing
/// the access of a folder somebody made by hand is their decision.</para>
/// </summary>
public static class WorkRootPolicy
{
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
                    + "so any account on this machine could redirect the files the worker writes and deletes there. Format the drive as NTFS, "
                    + $"or accept the risk by setting the machine-wide variable {WorkLocation.AllowUnprotectedVariable}=1 and restarting the service.");
            }

            return new(facts.Exists ? WorkRootAction.Use : WorkRootAction.Create,
                Notice: $"{root} is on a drive without access control, used because {WorkLocation.AllowUnprotectedVariable} is set: "
                    + "any account on this machine can redirect the files the worker writes and deletes there.");
        }

        if (!facts.Exists)
        {
            return new(WorkRootAction.Create);
        }

        if (!WorkAccess.IsTrusted(facts.Owner))
        {
            return Refuse($"{root} is owned by {facts.Owner ?? "an unknown account"}, not SYSTEM or Administrators, so it was not trusted. {Fix(root)}");
        }

        var writers = WorkAccess.UntrustedWriters(facts.Access ?? [new AccessEntry(WorkAccess.Everyone, 0x1F01FF)]);
        if (writers.Count == 0)
        {
            return new(WorkRootAction.Use);
        }

        var who = string.Join(", ", writers);
        return string.Equals(facts.Owner, WorkAccess.LocalSystem, StringComparison.OrdinalIgnoreCase)
            ? new(WorkRootAction.LockDown,
                Notice: $"{root} could be changed by other accounts ({who}); its access was reset so only SYSTEM and Administrators can use it.")
            : Refuse($"{root} can be changed by accounts other than SYSTEM and Administrators ({who}), so it was not used. {Fix(root)}");
    }

    private static WorkRootVerdict Refuse(string problem) => new(WorkRootAction.Refuse, problem);

    private static string Fix(string root) =>
        $"From an administrator command prompt, run: icacls \"{root}\" /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F "
        + $"then: icacls \"{root}\" /setowner *S-1-5-32-544 and restart the service. Files already inside keep their own access, "
        + "so if it holds nothing you need, delete the folder instead and let the service create it.";
}

/// <summary>What is already at a job's folder path when the service goes to create it.</summary>
public sealed record JobFolderFacts(bool IsLink, bool IsDirectory, string? Owner, bool VolumeHasAccessControl = true);

public enum JobFolderAction { Create, ClearThenCreate, Refuse }

public sealed record JobFolderDecision(JobFolderAction Action, string? Problem = null);

/// <summary>
/// A job's folder is created fresh, never adopted. Job ids are predictable, so anything already at
/// <c>job-&lt;id&gt;</c> could have been put there to be written through. Only a plain folder an
/// earlier run of the service left behind is cleared away; anything else stops the job.
/// </summary>
public static class JobFolderPolicy
{
    public static JobFolderDecision Decide(string path, JobFolderFacts? existing)
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

        return !existing.VolumeHasAccessControl || WorkAccess.IsTrusted(existing.Owner)
            ? new(JobFolderAction.ClearThenCreate)
            : new(JobFolderAction.Refuse,
                $"{path} was made by another account, so the job was not started there. Delete it from an administrator prompt.");
    }
}
