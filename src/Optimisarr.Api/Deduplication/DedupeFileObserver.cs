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
/// </summary>
internal static class DedupeFileObserver
{
    public static async Task<DedupeFileObservation> ObserveAsync(DedupeFileSnapshot file, CancellationToken token)
    {
        var info = new FileInfo(file.Path);
        var isLink = IsLinked(file.Root, file.Path, info);
        if (!isLink && !info.Exists) return Absent(file);

        var identity = ExactFileIdentity.Read(file.Path);
        var size = info.Exists ? info.Length : 0;
        var modified = info.Exists ? info.LastWriteTimeUtc.Ticks : 0;
        var hash = isLink || identity is null ? null : await ReadFullHashAsync(info, identity, size, modified, token);
        var quarantineFolder = file.QuarantinePath is { } destination ? NearestExistingFolder(destination) : null;
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
            IsIndependentFile: DedupeFileEligibility.IsIndependentFile(Path.GetRelativePath(file.Root, file.Path)));
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
        ParentWritable: false, QuarantineDestinationExists: false, QuarantineVolumeId: null, IsIndependentFile: false);

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
    private static async Task<string?> ReadFullHashAsync(FileInfo info, string identity, long size, long modified, CancellationToken token)
    {
        try
        {
            await using var stream = new FileStream(info.FullName, new FileStreamOptions
            {
                Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read, Options = FileOptions.SequentialScan | FileOptions.Asynchronous,
            });
            if (ExactFileIdentity.Read(stream.SafeFileHandle) != identity) return null;
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token));
            info.Refresh();
            return info.Exists && info.Length == size && info.LastWriteTimeUtc.Ticks == modified
                && ExactFileIdentity.Read(info.FullName) == identity ? hash : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? NearestExistingFolder(string path)
    {
        for (var folder = Path.GetDirectoryName(path); folder is not null; folder = Path.GetDirectoryName(folder))
            if (Directory.Exists(folder)) return folder;
        return null;
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
