using System.Runtime.InteropServices;

namespace Optimisarr.Core.IO;

/// <summary>
/// Reads how many directory entries point at a file's inode — its hard link count.
///
/// .NET has no such API. <see cref="FileSystemInfo.LinkTarget"/> and
/// <see cref="File.ResolveLinkTarget"/> describe <em>symbolic</em> links, which are a different
/// thing entirely: a symlink is its own file pointing at this one, and replacing the target does
/// not rewrite anyone else's copy. A hard link is the same inode under another name, so replacing
/// the file changes what every other name resolves to — which is what a torrent client still
/// seeding an imported file would notice.
///
/// Reading it therefore means calling the platform's own stat. Linux uses <c>statx</c> rather than
/// <c>stat</c> because its structure is identical on every architecture, while <c>struct stat</c>
/// is not — a raw <c>stat</c> binding correct on x86-64 can read the wrong offset on arm64, and
/// this image ships for both.
/// </summary>
public static class HardLinkProbe
{
    /// <summary>
    /// The number of names pointing at <paramref name="path"/>, or <c>null</c> when it cannot be
    /// determined — the file is gone, the filesystem or platform will not say, or the call failed.
    ///
    /// Never returns zero on failure. Zero would read as "definitely not linked", and every caller
    /// of this treats an unknown count as a reason to leave a file alone.
    /// </summary>
    public static int? CountLinks(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            if (OperatingSystem.IsLinux())
            {
                return LinuxLinkCount(path);
            }

            return OperatingSystem.IsMacOS() ? DarwinLinkCount(path) : null;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            // An unexpected libc. Unknown is the honest answer and the safe one.
            return null;
        }
    }

    private static int? LinuxLinkCount(string path)
    {
        // AT_FDCWD, because the path is absolute; no flags, so a symlink resolves to its target,
        // whose link count is the one that matters.
        if (Statx(AtFdCwd, path, 0, StatxNlink, out var stat) != 0)
        {
            return null;
        }

        // statx fills only what it was asked for and reports back what it actually supplied. A
        // filesystem that does not report link counts sets no bit here, and that is not an error.
        return (stat.Mask & StatxNlink) == 0 ? null : AtLeastOne((int)stat.NLink);
    }

    private static int? DarwinLinkCount(string path) =>
        Stat(path, out var stat) == 0 ? AtLeastOne(stat.NLink) : null;

    /// <summary>
    /// A file that exists has at least one name, so a reported zero is a filesystem that does not
    /// really track this rather than a fact about the file. Unknown is the honest answer, and the
    /// only one callers treat as a reason to leave a file alone.
    /// </summary>
    private static int? AtLeastOne(int links) => links < 1 ? null : links;

    private const int AtFdCwd = -100;
    private const uint StatxNlink = 0x0000_0004;

    /// <summary>
    /// Only the two fields this needs, at their fixed offsets in the 256-byte kernel structure.
    /// Declaring the whole of it would add nothing but more opportunities to mistype an offset.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct StatxResult
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(16)] public uint NLink;
    }

    [StructLayout(LayoutKind.Explicit, Size = 144)]
    private struct DarwinStat
    {
        [FieldOffset(6)] public ushort NLink;
    }

    [DllImport("libc", EntryPoint = "statx", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int Statx(
        int directoryFd, string path, int flags, uint mask, out StatxResult result);

    [DllImport("libc", EntryPoint = "stat", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int Stat(string path, out DarwinStat result);
}
