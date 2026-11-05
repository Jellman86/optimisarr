using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Optimisarr.Api.Library;
using Optimisarr.Core.Deduplication;
using Optimisarr.Core.IO;

namespace Optimisarr.Api.Deduplication;

/// <summary>
/// Reads what <see cref="DedupeActionPreflight"/> judges about one planned file, straight from the
/// filesystem and never from cached inventory. It only reads. Anything it cannot establish is
/// reported in the form the rule refuses: an unknown link count, volume or hash is never assumed.
/// Only Linux and macOS report a volume and write access, so no other platform can pass.
/// <para>
/// This is evidence, not a guarantee. Equal volumes do not prove a rename will work: separate bind
/// mounts of one filesystem share a device yet refuse rename, and overlayfs can report different
/// devices within one mount. The move itself must be rename-only and refuse EXDEV, as
/// <c>RenameOnly</c> does, and must recheck the file around that rename.
/// </para>
/// </summary>
internal static class DedupeFileObserver
{
    // A write in the same clock tick as the stamp would not change it, so a file touched this
    // recently cannot be proved stable by comparing stamps. It is judged again on a later attempt.
    private static readonly TimeSpan ChangeSettle = TimeSpan.FromSeconds(2);

    /// <param name="changeSettle">How long a file must have been unchanged to be read; tests shorten it.</param>
    /// <param name="afterRead">Runs between the read and the final checks, so tests can interfere there.</param>
    public static async Task<DedupeFileObservation> ObserveAsync(DedupeFileSnapshot file, CancellationToken token,
        TimeSpan? changeSettle = null, Func<Task>? afterRead = null)
    {
        var info = new FileInfo(file.Path);
        var isLink = IsLinked(file.Root, file.Path, info);
        if (!isLink && !info.Exists) return Absent(file);

        var identity = ExactFileIdentity.Read(file.Path);
        var size = info.Exists ? info.Length : 0;
        var modified = info.Exists ? info.LastWriteTimeUtc.Ticks : 0;
        var hash = isLink || identity is null
            ? null : await ReadFullHashAsync(info, identity, size, modified, changeSettle ?? ChangeSettle, afterRead, token);
        // Whatever the read saw, the path it was reached by must still be free of links now.
        isLink = isLink || IsLinked(file.Root, file.Path, info);
        if (isLink) hash = null;
        var (quarantineSafe, quarantineFolder) = file.QuarantinePath is { } destination
            ? WalkQuarantinePath(file.Root, destination) : (false, null);
        return new DedupeFileObservation(
            file.MediaFileId,
            Exists: true,
            IsLink: isLink,
            HardLinkCount: HardLinkProbe.CountLinks(file.Path) ?? 0,
            Identity: identity,
            SizeBytes: size,
            ModifiedUtcTicks: modified,
            FreshSha256: hash,
            VolumeId: VolumeOf(identity),
            ParentWritable: CanWriteFolder(info.DirectoryName) && (file.QuarantinePath is null || CanWriteFolder(quarantineFolder)),
            QuarantineDestinationExists: file.QuarantinePath is { } occupied && Occupied(occupied),
            QuarantineVolumeId: quarantineFolder is null ? null : VolumeOf(ExactFileIdentity.Read(quarantineFolder)),
            QuarantinePathSafe: quarantineSafe,
            // The whole path, because a library rooted at a disc's BDMV folder is still a disc.
            IsIndependentFile: DedupeFileEligibility.IsIndependentFile(file.Path));
    }

    /// <summary>The filesystem part of a file identity; null when the platform gives none.</summary>
    internal static string? VolumeOf(string? identity)
    {
        var parts = identity?.Split(':');
        return parts switch
        {
            ["linux", var major, var minor, _] => $"linux:{major}:{minor}",
            ["darwin", var device, _] => $"darwin:{device}",
            _ => null,
        };
    }

    private static DedupeFileObservation Absent(DedupeFileSnapshot file) => new(file.MediaFileId, Exists: false, IsLink: false,
        HardLinkCount: 0, Identity: null, SizeBytes: 0, ModifiedUtcTicks: 0, FreshSha256: null, VolumeId: null,
        ParentWritable: false, QuarantineDestinationExists: false, QuarantineVolumeId: null, QuarantinePathSafe: false,
        IsIndependentFile: false);

    // A link at the file or at any folder inside the library root redirects the path elsewhere.
    private static bool IsLinked(string root, string path, FileInfo info)
    {
        try
        {
            if (info.LinkTarget is not null) return true;
            return info.Exists && !ExactDuplicateScanner.SafePath(root, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    // Null unless the same unchanged file was read from start to end: a hash of bytes that may
    // have moved underneath the read proves nothing about the file that would be quarantined.
    // Size and modification time alone cannot show that, because a writer can restore the time.
    private static async Task<string?> ReadFullHashAsync(FileInfo info, string identity, long size, long modified,
        TimeSpan settle, Func<Task>? afterRead, CancellationToken token)
    {
        try
        {
            await using var stream = new FileStream(info.FullName, new FileStreamOptions
            {
                Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read, Options = FileOptions.SequentialScan | FileOptions.Asynchronous,
            });
            if (ExactFileIdentity.Read(stream.SafeFileHandle) != identity) return null;
            if (ExactFileIdentity.ChangeStamp(stream.SafeFileHandle) is not { } before) return null;
            var changedAt = DateTimeOffset.FromUnixTimeSeconds(before.Seconds).AddTicks(before.Nanoseconds / 100);
            if (DateTimeOffset.UtcNow - changedAt < settle) return null;
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token));
            if (afterRead is not null) await afterRead();
            info.Refresh();
            return ExactFileIdentity.ChangeStamp(stream.SafeFileHandle) == before
                && info.Exists && info.Length == size && info.LastWriteTimeUtc.Ticks == modified
                && ExactFileIdentity.Read(info.FullName) == identity ? hash : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Walks down from the library root, because looking upward from the destination would step
    // over a dangling link or a file in the way and report the folder above it as fine.
    private static (bool Safe, string? NearestFolder) WalkQuarantinePath(string root, string destination)
    {
        try
        {
            var relative = Path.GetRelativePath(root, Path.GetDirectoryName(destination)!);
            if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || Path.IsPathRooted(relative)) return (false, null);
            var folder = root;
            if (relative == ".") return (true, folder);
            foreach (var part in relative.Split(Path.DirectorySeparatorChar))
            {
                var next = Path.Combine(folder, part);
                if (new FileInfo(next).LinkTarget is not null || File.Exists(next)) return (false, null);
                if (!Directory.Exists(next)) return (true, folder);
                folder = next;
            }
            return (true, folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (false, null);
        }
    }

    // A dangling link is occupied too, although File.Exists follows it and reports nothing there.
    private static bool Occupied(string path)
    {
        try { return Path.Exists(path) || new FileInfo(path).LinkTarget is not null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    private static bool CanWriteFolder(string? folder)
    {
        const int writeAndSearch = 2 | 1;
        if (folder is null || OperatingSystem.IsWindows()) return false;
        try { return Access(folder, writeAndSearch) == 0; }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    [DllImport("libc", EntryPoint = "access", CharSet = CharSet.Ansi)]
    private static extern int Access(string path, int mode);
}
