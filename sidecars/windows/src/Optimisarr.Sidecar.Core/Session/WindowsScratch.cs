using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Optimisarr.Sidecar.Core.Session;

/// <summary>
/// A job's folder, and anything in it, held open as itself while it is judged and removed.
///
/// <para>Only the top is opened by path. Below it, each folder is listed through its own handle and
/// each entry opened relative to that handle, so nothing is looked up by a path that would be
/// resolved again. Holding a folder stops it being renamed or deleted, but an emptied folder can
/// still be turned into a junction in place by anyone allowed to write its attributes; a walk by
/// path would then follow it out of the job folder. Listed and opened through the handle, the
/// converted folder shows only its own, empty, contents.</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsScratchEntry : IScratchEntry
{
    // DELETE takes part in sharing, so leaving FILE_SHARE_DELETE out stops anyone renaming the
    // entry away while it is held; READ_CONTROL lets its owner be read; list access lets a folder
    // be listed through this same handle.
    private const uint Access = WindowsFileApi.ListDirectory | WindowsFileApi.Delete | WindowsFileApi.ReadAttributes | WindowsFileApi.ReadControl;

    private readonly SafeFileHandle _handle;
    private readonly string _path;
    private readonly WindowsFileIdentity _identity;

    private WindowsScratchEntry(string path, SafeFileHandle handle)
    {
        _path = path;
        _handle = handle;
        try { _identity = WindowsFileApi.Identify(handle); }
        catch { handle.Dispose(); throw; }
    }

    /// <summary>The entry, or null when nothing is there.</summary>
    public static WindowsScratchEntry? TryOpen(string path)
    {
        var handle = WindowsFileApi.TryOpen(path, Access, WindowsFileApi.ShareReadWrite, out var error);
        if (handle is not null) return new(path, handle);
        if (WindowsFileApi.IsMissing(error)) return null;
        throw new IOException($"{path} could not be opened ({WindowsFileApi.Describe(error)}).");
    }

    /// <summary>
    /// An entry over a handle the caller already opened and checked; it is disposed with the
    /// entry. The handle needs list and delete access for the entry to be emptied and removed.
    /// </summary>
    public static WindowsScratchEntry Adopt(string path, SafeFileHandle handle) => new(path, handle);

    internal WindowsFileIdentity Identity => _identity;

    public ScratchEntryKind Kind => _identity.IsLink ? ScratchEntryKind.Link
        : _identity.IsDirectory ? ScratchEntryKind.Directory
        : ScratchEntryKind.File;

    public IEnumerable<string> Names()
    {
        if (Kind != ScratchEntryKind.Directory) throw new IOException($"{_path} is not a folder.");
        return WindowsFileApi.ListNames(_handle);
    }

    public IScratchEntry Open(string name)
    {
        var path = Path.Combine(_path, name);
        var handle = WindowsFileApi.OpenChild(_handle, name, Access, WindowsFileApi.ShareReadWrite, out var error)
            ?? throw new IOException(WindowsFileApi.IsMissing(error)
                ? $"{path} disappeared while its folder was being removed."
                : $"{path} could not be opened ({WindowsFileApi.Describe(error)}).");
        var child = new WindowsScratchEntry(path, handle);
        if (child._identity.Volume != _identity.Volume)
        {
            child.Dispose();
            throw new IOException($"{path} is on another drive, so it was not removed.");
        }
        return child;
    }

    public void Delete() => WindowsFileApi.DeleteThroughHandle(_handle);

    /// <summary>What a leftover at this path is, for <see cref="JobFolderPolicy"/>.</summary>
    public JobFolderFacts Facts()
    {
        if (!WindowsFileApi.HasAccessControl(_handle)) return new(_identity.IsLink, _identity.IsDirectory, Owner: null, VolumeHasAccessControl: false);
        var (owner, access) = WindowsFileApi.ReadAccess(_handle);
        return new(_identity.IsLink, _identity.IsDirectory, owner, Access: access);
    }

    public void Dispose() => _handle.Dispose();
}

/// <summary>The Windows side of <see cref="JobScratch"/>.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsScratch
{
    /// <summary>
    /// Creates the job's folder only if nothing is there, so it inherits the work folder's
    /// administrator-only access and nothing planted in advance is adopted. A plain folder an
    /// earlier run left behind, that no other account can change, is removed first; anything else
    /// stops the job.
    /// </summary>
    public static string? CreateJobFolder(string path)
    {
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var error = WindowsFileApi.CreateDirectory(path);
                if (error == 0) return Confirm(path);
                if (error != WindowsFileApi.ErrorAlreadyExists)
                    return $"{path} could not be created ({WindowsFileApi.Describe(error)}), so the job was not started.";

                using var existing = WindowsScratchEntry.TryOpen(path);
                if (existing is null) continue;
                var decision = JobFolderPolicy.Decide(path, existing.Facts(), WindowsFileApi.CurrentUser);
                if (decision.Action == JobFolderAction.Refuse) return decision.Problem;
                ScratchTree.Delete(existing);
            }

            return $"{path} came back after it was cleared, so the job was not started there.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return $"{path} could not be prepared ({error.Message}), so the job was not started.";
        }
    }

    public static void DeleteTree(string path)
    {
        using var entry = WindowsScratchEntry.TryOpen(path);
        if (entry is not null) ScratchTree.Delete(entry);
    }

    private static string? Confirm(string path)
    {
        using var created = WindowsScratchEntry.TryOpen(path);
        return created?.Kind == ScratchEntryKind.Directory
            ? null
            : $"{path} was replaced as soon as it was created, so the job was not started there.";
    }
}
