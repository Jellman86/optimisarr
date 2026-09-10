using System.Runtime.InteropServices;
using Optimisarr.Core.IO;

namespace Optimisarr.Tests;

/// <summary>
/// The rule that acts on a hardlink count is pure and tested separately; this proves the one part
/// that cannot be — reading the count off a real file. .NET exposes no link count of its own (its
/// only link APIs are for symlinks), so this goes through the platform's own stat call and the
/// answer has to be checked against links this test actually creates.
/// </summary>
public sealed class HardLinkProbeTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("optimisarr-links-").FullName;

    [Fact]
    public void A_plain_file_reports_a_single_link()
    {
        var file = WriteFile("movie.mkv");

        Assert.Equal(1, HardLinkProbe.CountLinks(file));
    }

    [Fact]
    public void A_hardlinked_file_reports_both_links()
    {
        var file = WriteFile("movie.mkv");
        var link = Path.Combine(_root, "seeding.mkv");
        CreateHardLink(file, link);

        // Both names are the same file, so both report the same count. Which one a library
        // discovered is irrelevant to whether replacing it would break the other.
        Assert.Equal(2, HardLinkProbe.CountLinks(file));
        Assert.Equal(2, HardLinkProbe.CountLinks(link));
    }

    [Fact]
    public void Removing_a_link_drops_the_count_again()
    {
        // The count is volatile, which is why eligibility is re-checked before a replacement
        // rather than trusted from the scan that discovered the file.
        var file = WriteFile("movie.mkv");
        var link = Path.Combine(_root, "seeding.mkv");
        CreateHardLink(file, link);
        File.Delete(link);

        Assert.Equal(1, HardLinkProbe.CountLinks(file));
    }

    [Fact]
    public void A_symlink_is_not_counted_as_a_hard_link()
    {
        // A symlink is a separate file pointing at this one; deleting or replacing the target does
        // not silently rewrite someone else's copy, so it is not what this rule is about.
        var file = WriteFile("movie.mkv");
        File.CreateSymbolicLink(Path.Combine(_root, "shortcut.mkv"), file);

        Assert.Equal(1, HardLinkProbe.CountLinks(file));
    }

    [Fact]
    public void A_missing_file_reports_an_undeterminable_count()
    {
        // Never zero, which would read as "definitely not linked" and let a vanished file through.
        Assert.Null(HardLinkProbe.CountLinks(Path.Combine(_root, "gone.mkv")));
    }

    private string WriteFile(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, "payload");
        return path;
    }

    private static void CreateHardLink(string existing, string created)
    {
        var result = Link(existing, created);
        Assert.True(result == 0,
            $"link() failed with errno {Marshal.GetLastWin32Error()} creating {created}");
    }

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link(string existing, string created);

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
