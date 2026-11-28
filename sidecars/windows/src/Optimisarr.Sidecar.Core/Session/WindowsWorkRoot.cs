using System.ComponentModel;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Optimisarr.Sidecar.Core.Session;

/// <summary>
/// The work folder, checked against <see cref="WorkRootPolicy"/> and then held open for as long as
/// the service runs.
///
/// <para><b>Every folder on the path is checked, not only the work folder.</b> A junction anywhere
/// above it redirects everything beneath, so <c>D:\data</c> swapped for a junction makes
/// <c>D:\data\OptimisarrWork</c> somewhere else entirely, however well that folder is locked. Each
/// component is opened as itself, never followed, and refused if it is a link.</para>
///
/// <para><b>Each folder is found inside the one before it, never by path.</b> Only the drive is
/// opened by name; every folder below is opened, or created, relative to its parent's handle. A
/// parent that is still empty when it is opened could otherwise be turned into a junction in
/// place before the next folder is looked up, and the service would create the work folder
/// wherever that junction points. Opened relative to the handle, it can only land in the parent
/// itself, and the parent then fails the link check below.</para>
///
/// <para><b>Each of those folders then stays open, without delete sharing.</b> Windows will not rename
/// or delete a folder while such a handle is held on it, so the path checked is the path used until
/// the service stops: none of its folders can be moved aside and replaced, and none can be turned
/// into a junction in place either, because each holds the next one and only an empty folder can
/// be converted. The work folder itself is kept from being empty by
/// <see cref="WorkRootPolicy.SentinelName"/>, which the service holds open. The cost is that the
/// work folder, the folders above it, and a RAM drive holding them cannot be renamed, removed or
/// cleanly unmounted while the service runs.</para>
///
/// <para>Rechecked before every check-in and every job, through the held handles. If the folder has
/// gone, its drive has been unmounted, or its access has been opened up, the check starts again from
/// scratch: a drive mounted late is picked up. A folder is rebuilt (see
/// <see cref="WorkRootAction.Rebuild"/>) only before the service first uses it, never under jobs
/// that may be running.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsWorkRoot : IDisposable
{
    // List access takes part in sharing; attribute and access-list reads alone would not.
    private const uint PinAccess = WindowsFileApi.ListDirectory | WindowsFileApi.ReadAttributes | WindowsFileApi.ReadControl;
    private const uint RebuildAccess = PinAccess | WindowsFileApi.WriteDac | WindowsFileApi.Delete;
    private const uint SentinelAccess = WindowsFileApi.ReadData | WindowsFileApi.ReadAttributes | WindowsFileApi.ReadControl;
    private const uint InspectAccess = WindowsFileApi.ReadAttributes | WindowsFileApi.ReadControl;

    private readonly Lock _gate = new();
    private readonly string _path;
    private readonly bool _allowUnprotected;
    private readonly bool _mayChange;
    private readonly Action<string>? _notice;
    private readonly Action<string>? _afterOpening;
    private List<SafeFileHandle>? _held;
    private SafeFileHandle? _sentinel;
    private bool _volumeHasAccessControl;
    private bool _used;

    private WindowsWorkRoot(string path, bool allowUnprotected, bool mayChange, Action<string>? notice, Action<string>? afterOpening)
    {
        _path = path;
        _allowUnprotected = allowUnprotected;
        _mayChange = mayChange;
        _notice = notice;
        _afterOpening = afterOpening;
    }

    /// <param name="path">A normalised work location, <c>X:\folder[\...]</c>, as <see cref="WorkLocation"/> returns.</param>
    /// <param name="mayChange">Whether this process may create, rebuild and hold the folder. Only
    /// the service may: a folder created by a pairing prompt would belong to whoever ran it.</param>
    /// <param name="notice">Told when the folder is rebuilt, or an unprotected drive is used.</param>
    public static WindowsWorkRoot Open(string path, bool allowUnprotected, bool mayChange, Action<string>? notice = null) =>
        Open(path, allowUnprotected, mayChange, notice, afterOpening: null);

    /// <param name="afterOpening">Called with each folder on the path once it is open, before the
    /// next is looked up: where a test plays another account changing it in between.</param>
    internal static WindowsWorkRoot Open(string path, bool allowUnprotected, bool mayChange, Action<string>? notice, Action<string>? afterOpening)
    {
        var root = new WindowsWorkRoot(path, allowUnprotected, mayChange, notice, afterOpening);
        root.Recheck();
        return root;
    }

    /// <summary>Why the folder cannot be used, as of the last check; null when it can.</summary>
    public string? Problem { get; private set; }

    /// <summary>Checks again, and returns why the folder cannot be used, or null.</summary>
    public string? Recheck()
    {
        lock (_gate)
        {
            if (_held is not null && StillSound(_held, _sentinel))
            {
                return Problem = null;
            }

            Release();
            return Problem = Establish();
        }
    }

    public void Dispose()
    {
        lock (_gate) Release();
    }

    /// <summary>
    /// The checks every use relies on, through handles already held: no folder on the path has
    /// become a link, the sentinel is held where the service needs it, and the work folder's own
    /// access is still sound.
    /// </summary>
    private bool StillSound(List<SafeFileHandle> held, SafeFileHandle? sentinel)
    {
        try
        {
            if (held.Any(handle => WindowsFileApi.Identify(handle).IsLink)) return false;
            if (_mayChange && sentinel is null) return false;
            return WorkRootPolicy.Decide(_path, Facts(held[^1], withEntries: false), _allowUnprotected).Action == WorkRootAction.Use;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return false;
        }
    }

    private string? Establish()
    {
        // A rebuild deletes the folder; the second pass then creates it fresh and checks it again.
        for (var pass = 0; pass < 2; pass++)
        {
            var problem = EstablishOnce(out var rebuilt);
            if (rebuilt) continue;
            if (problem is null) _used = true;
            return problem;
        }

        return $"{_path} could be changed by other accounts again as soon as it was rebuilt, so it was not used.";
    }

    private string? EstablishOnce(out bool rebuilt)
    {
        rebuilt = false;
        var held = new List<SafeFileHandle>();
        SafeFileHandle? sentinel = null;
        try
        {
            var drive = _path[..3];
            var volume = WindowsFileApi.TryOpen(drive, PinAccess, WindowsFileApi.ShareReadWrite, out var error);
            if (volume is null)
            {
                return $"The drive {drive} cannot be opened ({WindowsFileApi.Describe(error)}). If it is a RAM drive, mount it; "
                    + "ordinary jobs resume once it is back, and quality-first work after the service is restarted.";
            }
            held.Add(volume);
            _volumeHasAccessControl = WindowsFileApi.HasAccessControl(volume);

            var components = _path[3..].Split('\\');
            var current = drive.TrimEnd('\\');
            for (var index = 0; index < components.Length; index++)
            {
                current += "\\" + components[index];
                var isWorkFolder = index == components.Length - 1;
                if (OpenComponent(held[^1], current, components[index], isWorkFolder, out var problem) is not { } handle) return problem;
                held.Add(handle);

                var identity = WindowsFileApi.Identify(handle);
                if (identity.IsLink)
                {
                    return WorkRootPolicy.Decide(_path, new(true, LinkedComponent: current), _allowUnprotected).Problem;
                }
                if (!identity.IsDirectory)
                {
                    return $"{current} is a file, not a folder, so it cannot hold the work folder.";
                }
                _afterOpening?.Invoke(current);
            }

            var root = held[^1];
            var verdict = WorkRootPolicy.Decide(_path, Facts(root, withEntries: true), _allowUnprotected);
            if (verdict.Action == WorkRootAction.Rebuild)
            {
                if (!_mayChange)
                {
                    return $"{_path} can be changed by other accounts, or holds leftovers they can change. "
                        + "The service deletes it and creates it again when it starts.";
                }
                if (_used)
                {
                    return $"{_path} became changeable by other accounts while the service was using it, so it was not used. "
                        + "Restart the service, which then deletes it and creates it again for SYSTEM and Administrators only.";
                }

                var expected = WindowsFileApi.Identify(root);
                held.RemoveAt(held.Count - 1);
                root.Dispose();
                Rebuild(held[^1], expected);
                _notice?.Invoke(verdict.Notice!);
                rebuilt = true;
                return null;
            }

            if (verdict.Action != WorkRootAction.Use)
            {
                return verdict.Problem ?? $"{_path} could not be created.";
            }

            if (_mayChange && (sentinel = OpenSentinel(root, out var sentinelProblem)) is null)
            {
                return sentinelProblem;
            }

            if (!StillSound(held, sentinel))
            {
                return $"{_path} changed while it was being checked, so it was not used.";
            }

            if (verdict.Notice is not null) _notice?.Invoke(verdict.Notice);
            _held = held;
            _sentinel = sentinel;
            held = [];
            sentinel = null;
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return $"{_path} could not be checked ({error.Message}).";
        }
        finally
        {
            foreach (var handle in held) handle.Dispose();
            sentinel?.Dispose();
        }
    }

    /// <summary>
    /// Opens one folder on the path as itself, inside the folder before it, creating it there first
    /// if it is missing and this process may. The work folder is created already locked, so there
    /// is no moment when it has the drive's open access; the folders above it are ordinary folders
    /// with whatever they inherit.
    /// </summary>
    private SafeFileHandle? OpenComponent(SafeFileHandle parent, string path, string name, bool isWorkFolder, out string? problem)
    {
        problem = null;
        var handle = WindowsFileApi.OpenChild(parent, name, PinAccess, WindowsFileApi.ShareReadWrite, out var error);
        if (handle is not null) return handle;
        if (!WindowsFileApi.IsMissing(error))
        {
            problem = $"{path} cannot be opened ({WindowsFileApi.Describe(error)}).";
            return null;
        }

        if (!_mayChange)
        {
            problem = $"{path} will be created when the service starts.";
            return null;
        }

        if (isWorkFolder)
        {
            var verdict = WorkRootPolicy.Decide(_path, new(Exists: false, VolumeHasAccessControl: _volumeHasAccessControl), _allowUnprotected);
            if (verdict.Action == WorkRootAction.Refuse)
            {
                problem = verdict.Problem;
                return null;
            }
        }

        handle = isWorkFolder && _volumeHasAccessControl
            ? WindowsFileApi.CreateAdministratorOnlyChildDirectory(parent, name, PinAccess, WindowsFileApi.ShareReadWrite, out error)
            : WindowsFileApi.CreateChildDirectory(parent, name, descriptor: null, PinAccess, WindowsFileApi.ShareReadWrite, out error);
        // Made by someone else in between: opened, and then judged like any existing folder.
        if (handle is null && error == WindowsFileApi.ErrorAlreadyExists)
        {
            handle = WindowsFileApi.OpenChild(parent, name, PinAccess, WindowsFileApi.ShareReadWrite, out error);
        }
        if (handle is null) problem = $"{path} could not be created ({WindowsFileApi.Describe(error)}).";
        return handle;
    }

    private WorkRootFacts Facts(SafeFileHandle root, bool withEntries)
    {
        if (!_volumeHasAccessControl) return new(true, VolumeHasAccessControl: false);
        var (owner, access) = WindowsFileApi.ReadAccess(root);
        // Named only when it will be refused for its owner: this runs before every check-in, and a
        // name lookup can wait on a domain controller.
        var ownerName = WorkAccess.IsTrusted(owner) ? null : WindowsFileApi.AccountName(owner);
        return new(true, Owner: owner, Access: access, Entries: withEntries ? Entries(root) : null, OwnerName: ownerName);
    }

    /// <summary>
    /// What is directly inside the work folder, listed and opened through its handle. Only the
    /// service's own names are opened: anything else is somebody's own file, judged by name alone.
    /// One of the service's names that cannot even be opened counts as owned by nobody it trusts.
    /// </summary>
    private List<WorkRootEntry> Entries(SafeFileHandle root)
    {
        var entries = new List<WorkRootEntry>();
        foreach (var name in WindowsFileApi.ListNames(root))
        {
            if (!WorkRootPolicy.IsServiceEntry(name))
            {
                entries.Add(new(name, IsLink: false, Owner: null, Access: null));
                continue;
            }

            using var entry = WindowsFileApi.OpenChild(root, name, InspectAccess, WindowsFileApi.ShareAll, out var error);
            if (entry is null)
            {
                if (!WindowsFileApi.IsMissing(error)) entries.Add(new(name, IsLink: false, Owner: null, Access: null));
                continue;
            }

            var (owner, access) = WindowsFileApi.ReadAccess(entry);
            entries.Add(new(name, WindowsFileApi.Identify(entry).IsLink, owner, access));
        }
        return entries;
    }

    /// <summary>
    /// Deletes a work folder that other accounts could change, and everything the service left in
    /// it, through handles: <see cref="WorkRootAction.Rebuild"/>. Its access is locked first, so
    /// nobody else can add to it while it is emptied. Deleting it, rather than only resetting its
    /// access, is what retires a handle another account opened on it before: that handle is left
    /// on a deleted folder, and the folder created in its place is a new one. Anything other than
    /// the service's own names appearing in the meantime stops the rebuild.
    /// </summary>
    private void Rebuild(SafeFileHandle parent, WindowsFileIdentity expected)
    {
        var handle = WindowsFileApi.OpenChild(parent, Path.GetFileName(_path), RebuildAccess, WindowsFileApi.ShareReadWrite, out var error)
            ?? throw new IOException($"{_path} could not be opened to rebuild it ({WindowsFileApi.Describe(error)}). "
                + "If another program has it open, close that program and restart the service.");
        using var root = WindowsScratchEntry.Adopt(_path, handle);
        if (!root.Identity.SameObject(expected) || root.Identity.IsLink)
        {
            throw new IOException($"{_path} changed while it was being rebuilt.");
        }

        WindowsFileApi.LockDown(handle);
        foreach (var name in root.Names().ToArray())
        {
            if (!WorkRootPolicy.IsServiceEntry(name))
            {
                throw new IOException($"{Path.Combine(_path, name)} appeared while the folder was being rebuilt, so nothing more was deleted.");
            }

            using var child = root.Open(name);
            ScratchTree.Delete(child);
        }

        root.Delete();
    }

    /// <summary>The sentinel file, created inside the work folder if it is missing, and held: see <see cref="WorkRootPolicy.SentinelName"/>.</summary>
    private SafeFileHandle? OpenSentinel(SafeFileHandle root, out string? problem)
    {
        var path = Path.Combine(_path, WorkRootPolicy.SentinelName);
        var handle = WindowsFileApi.OpenOrCreateChildFile(root, WorkRootPolicy.SentinelName, SentinelAccess, WindowsFileApi.ShareReadWrite, out var error);
        if (handle is null)
        {
            problem = $"{path} could not be created ({WindowsFileApi.Describe(error)}), so the work folder was not used.";
            return null;
        }

        var identity = WindowsFileApi.Identify(handle);
        if (identity.IsLink || identity.IsDirectory || identity.Links != 1)
        {
            handle.Dispose();
            problem = $"{path} is not the plain file the service keeps there, so the work folder was not used. "
                + "Delete it from an administrator prompt and restart the service.";
            return null;
        }

        problem = null;
        return handle;
    }

    private void Release()
    {
        foreach (var handle in _held ?? []) handle.Dispose();
        _held = null;
        _sentinel?.Dispose();
        _sentinel = null;
    }
}
