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
/// <para><b>Each of those folders then stays open, without delete sharing.</b> Windows will not rename
/// or delete a folder while such a handle is held on it, so the path checked is the path used until
/// the service stops: none of its folders can be moved aside and replaced. The cost is that the work
/// folder, the folders above it, and a RAM drive holding them cannot be renamed, removed or cleanly
/// unmounted while the service runs.</para>
///
/// <para>Rechecked before every check-in and every job, through the held handles. If the folder has
/// gone, its drive has been unmounted, or its access has been opened up, the check starts again from
/// scratch: a drive mounted late is picked up, and a folder the service owns is locked again.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsWorkRoot : IDisposable
{
    // List access takes part in sharing; attribute and access-list reads alone would not.
    private const uint PinAccess = WindowsFileApi.ListDirectory | WindowsFileApi.ReadAttributes | WindowsFileApi.ReadControl;

    private readonly Lock _gate = new();
    private readonly string _path;
    private readonly bool _allowUnprotected;
    private readonly bool _mayChange;
    private readonly Action<string>? _notice;
    private List<SafeFileHandle>? _held;
    private bool _volumeHasAccessControl;

    private WindowsWorkRoot(string path, bool allowUnprotected, bool mayChange, Action<string>? notice)
    {
        _path = path;
        _allowUnprotected = allowUnprotected;
        _mayChange = mayChange;
        _notice = notice;
    }

    /// <param name="path">A normalised work location, <c>X:\folder[\...]</c>, as <see cref="WorkLocation"/> returns.</param>
    /// <param name="mayChange">Whether this process may create the folder or reset its access. Only
    /// the service may: a folder created by a pairing prompt would belong to whoever ran it.</param>
    /// <param name="notice">Told when the folder's access is reset, or an unprotected drive is used.</param>
    public static WindowsWorkRoot Open(string path, bool allowUnprotected, bool mayChange, Action<string>? notice = null)
    {
        var root = new WindowsWorkRoot(path, allowUnprotected, mayChange, notice);
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
            if (_held is not null && StillSound())
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

    private bool StillSound()
    {
        try
        {
            if (_held!.Any(handle => WindowsFileApi.Identify(handle).IsLink)) return false;
            return WorkRootPolicy.Decide(_path, Facts(_held![^1]), _allowUnprotected).Action == WorkRootAction.Use;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return false;
        }
    }

    private string? Establish()
    {
        var held = new List<SafeFileHandle>();
        try
        {
            var drive = _path[..3];
            using (var volume = WindowsFileApi.TryOpen(drive, WindowsFileApi.ReadAttributes, WindowsFileApi.ShareReadWrite, out var error))
            {
                if (volume is null)
                {
                    return $"The drive {drive} cannot be opened ({WindowsFileApi.Describe(error)}). If it is a RAM drive, mount it; "
                        + "ordinary jobs resume once it is back, and quality-first work after the service is restarted.";
                }
                _volumeHasAccessControl = WindowsFileApi.HasAccessControl(volume);
            }

            var components = _path[3..].Split('\\');
            var current = drive.TrimEnd('\\');
            for (var index = 0; index < components.Length; index++)
            {
                current += "\\" + components[index];
                var isWorkFolder = index == components.Length - 1;
                if (OpenComponent(current, isWorkFolder, out var problem) is not { } handle) return problem;
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
            }

            var root = held[^1];
            var verdict = WorkRootPolicy.Decide(_path, Facts(root), _allowUnprotected);
            if (verdict.Action == WorkRootAction.LockDown)
            {
                if (!_mayChange)
                {
                    return $"{_path} can be changed by other accounts. The service resets its access when it starts.";
                }

                LockDown(root);
                _notice?.Invoke(verdict.Notice!);
                verdict = WorkRootPolicy.Decide(_path, Facts(root), _allowUnprotected);
                if (verdict.Action != WorkRootAction.Use)
                {
                    return $"{_path} could not be locked to SYSTEM and Administrators. {verdict.Problem ?? verdict.Notice}";
                }
            }

            if (verdict.Action != WorkRootAction.Use)
            {
                return verdict.Problem ?? $"{_path} could not be created.";
            }

            if (verdict.Notice is not null) _notice?.Invoke(verdict.Notice);
            _held = held;
            held = [];
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return $"{_path} could not be checked ({error.Message}).";
        }
        finally
        {
            foreach (var handle in held) handle.Dispose();
        }
    }

    /// <summary>
    /// Opens one folder on the path as itself, creating it first if it is missing and this process
    /// may. The work folder is created already locked, so there is no moment when it has the
    /// drive's open access; the folders above it are ordinary folders with whatever they inherit.
    /// </summary>
    private SafeFileHandle? OpenComponent(string path, bool isWorkFolder, out string? problem)
    {
        problem = null;
        var handle = WindowsFileApi.TryOpen(path, PinAccess, WindowsFileApi.ShareReadWrite, out var error);
        if (handle is not null) return handle;
        if (!WindowsFileApi.IsMissing(error))
        {
            problem = $"{path} cannot be opened ({WindowsFileApi.Describe(error)}).";
            return null;
        }

        if (!_mayChange)
        {
            problem = $"{path} does not exist yet. The service creates it when it starts.";
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

        var created = isWorkFolder && _volumeHasAccessControl
            ? WindowsFileApi.CreateAdministratorOnlyDirectory(path)
            : WindowsFileApi.CreateDirectory(path, descriptor: null);
        if (created is not (0 or WindowsFileApi.ErrorAlreadyExists))
        {
            problem = $"{path} could not be created ({WindowsFileApi.Describe(created)}).";
            return null;
        }

        handle = WindowsFileApi.TryOpen(path, PinAccess, WindowsFileApi.ShareReadWrite, out error);
        if (handle is null) problem = $"{path} cannot be opened ({WindowsFileApi.Describe(error)}).";
        return handle;
    }

    private WorkRootFacts Facts(SafeFileHandle root)
    {
        if (!_volumeHasAccessControl) return new(true, VolumeHasAccessControl: false);
        var (owner, access) = WindowsFileApi.ReadAccess(root);
        return new(true, Owner: owner, Access: access);
    }

    /// <summary>Through a second handle with the right to change access, proved to be the same folder.</summary>
    private void LockDown(SafeFileHandle root)
    {
        using var writable = WindowsFileApi.TryOpen(_path, WindowsFileApi.ReadAttributes | WindowsFileApi.ReadControl | WindowsFileApi.WriteDac,
                                 WindowsFileApi.ShareReadWrite, out var error)
                             ?? throw new IOException($"{_path} could not be opened to reset its access ({WindowsFileApi.Describe(error)}).");
        if (!WindowsFileApi.Identify(writable).SameObject(WindowsFileApi.Identify(root)))
        {
            throw new IOException($"{_path} changed while its access was being reset.");
        }

        WindowsFileApi.LockDown(writable);
    }

    private void Release()
    {
        foreach (var handle in _held ?? []) handle.Dispose();
        _held = null;
    }
}
