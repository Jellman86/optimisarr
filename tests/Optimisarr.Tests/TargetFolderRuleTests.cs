using Optimisarr.Core.Library;

namespace Optimisarr.Tests;

public sealed class TargetFolderRuleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "optimisarr-target-folder-" + Guid.NewGuid().ToString("N"));
    private readonly string _work;
    private readonly string _quarantine;
    private readonly LibraryFolder[] _libraries;

    public TargetFolderRuleTests()
    {
        _work = Folder("work");
        _quarantine = Folder("trash");
        _libraries = [new LibraryFolder("TV", Folder("media", "tv")), new LibraryFolder("Film", Folder("media", "films"))];
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Check_allows_a_folder_outside_every_library_the_working_space_and_quarantine()
    {
        var verdict = TargetFolderRule.Check(Folder("optimised", "tv"), _libraries, _work, _quarantine);

        Assert.True(verdict.IsAllowed);
        Assert.Equal("none", verdict.WireValue);
    }

    [Fact]
    public void Check_refuses_a_folder_inside_the_library_it_belongs_to_and_names_that_library()
    {
        var verdict = TargetFolderRule.Check(Folder("media", "tv", "optimised"), _libraries, _work, _quarantine);

        Assert.Equal(TargetFolderRefusal.InsideLibrary, verdict.Refusal);
        Assert.Equal("TV", verdict.LibraryName);
        Assert.Contains("inside the TV library", verdict.Describe("/x"));
    }

    [Fact]
    public void Check_refuses_another_librarys_own_folder()
    {
        var verdict = TargetFolderRule.Check(Path.Combine(_root, "media", "films") + Path.DirectorySeparatorChar, _libraries, _work, _quarantine);

        Assert.Equal(TargetFolderRefusal.InsideLibrary, verdict.Refusal);
        Assert.Equal("Film", verdict.LibraryName);
    }

    [Fact]
    public void Check_refuses_the_working_space_and_quarantine_and_anything_inside_them()
    {
        Assert.Equal(TargetFolderRefusal.WorkFolder, TargetFolderRule.Check(_work, _libraries, _work, _quarantine).Refusal);
        Assert.Equal(TargetFolderRefusal.WorkFolder, TargetFolderRule.Check(Folder("work", "kept"), _libraries, _work, _quarantine).Refusal);
        Assert.Equal(TargetFolderRefusal.Quarantine, TargetFolderRule.Check(_quarantine, _libraries, _work, _quarantine).Refusal);
        Assert.Equal(TargetFolderRefusal.Quarantine, TargetFolderRule.Check(Folder("trash", "kept"), _libraries, _work, _quarantine).Refusal);
    }

    [Fact]
    public void Check_does_not_mistake_a_folder_that_only_shares_a_name_prefix_for_one_inside()
    {
        var verdict = TargetFolderRule.Check(Folder("media", "tv-optimised"), _libraries, _work, _quarantine);

        Assert.True(verdict.IsAllowed);
    }

    [Fact]
    public void Check_refuses_a_path_that_climbs_back_into_a_library()
    {
        var climbing = Path.Combine(_root, "optimised", "..", "media", "tv", "out");

        Assert.Equal(TargetFolderRefusal.InsideLibrary, TargetFolderRule.Check(climbing, _libraries, _work, _quarantine).Refusal);
    }

    [Fact]
    public void ResolveLinks_follows_a_symbolic_link_so_a_link_into_a_library_is_refused()
    {
        var link = Path.Combine(_root, "looks-outside");
        Directory.CreateSymbolicLink(link, _libraries[0].Path);

        var resolved = TargetFolderRule.ResolveLinks(Path.Combine(link, "optimised"));

        Assert.Equal(TargetFolderRefusal.InsideLibrary, TargetFolderRule.Check(resolved, _libraries, _work, _quarantine).Refusal);
    }

    [Fact]
    public void ResolveLinks_follows_a_link_whose_target_goes_through_another_link()
    {
        // alias → media, out → alias/tv: following "out" once lands on a path that still has a link in it.
        var alias = Path.Combine(_root, "alias");
        Directory.CreateSymbolicLink(alias, Path.Combine(_root, "media"));
        var link = Path.Combine(_root, "out");
        Directory.CreateSymbolicLink(link, Path.Combine(alias, "tv"));

        var resolved = TargetFolderRule.ResolveLinks(Path.Combine(link, "optimised"));

        Assert.Equal(Path.Combine(TargetFolderRule.ResolveLinks(_libraries[0].Path), "optimised"), resolved);
        Assert.Equal(TargetFolderRefusal.InsideLibrary, TargetFolderRule.Check(resolved, _libraries, _work, _quarantine).Refusal);
    }

    [Fact]
    public void ResolveLinks_judges_a_relative_link_from_where_it_really_is_not_from_the_path_that_reached_it()
    {
        // save → mnt/out, mnt → pool/snap, pool/snap/out → ../../media/tv. The kernel reads the last
        // link from pool/snap, so save lands in the TV library. Collapsing ".." against mnt as text
        // would put it outside the root and let it pass.
        var snap = Folder("pool", "snap");
        Directory.CreateSymbolicLink(Path.Combine(snap, "out"), Path.Combine("..", "..", "media", "tv"));
        Directory.CreateSymbolicLink(Path.Combine(_root, "mnt"), snap);
        var save = Path.Combine(_root, "save");
        Directory.CreateSymbolicLink(save, Path.Combine(_root, "mnt", "out"));

        var resolved = TargetFolderRule.ResolveLinks(Path.Combine(save, "optimised"));

        Assert.Equal(Path.Combine(TargetFolderRule.ResolveLinks(_libraries[0].Path), "optimised"), resolved);
        Assert.Equal(TargetFolderRefusal.InsideLibrary, TargetFolderRule.Check(resolved, _libraries, _work, _quarantine).Refusal);
    }

    [Fact]
    public void ResolveLinks_climbs_out_of_a_link_from_where_the_link_points()
    {
        // up → media/tv/season, so up/../extra is media/tv/extra on disk, not <root>/extra.
        var season = Folder("media", "tv", "season");
        var up = Path.Combine(_root, "up");
        Directory.CreateSymbolicLink(up, season);

        var resolved = TargetFolderRule.ResolveLinks(up + Path.DirectorySeparatorChar + ".." + Path.DirectorySeparatorChar + "extra");

        Assert.Equal(Path.Combine(TargetFolderRule.ResolveLinks(_libraries[0].Path), "extra"), resolved);
    }

    [Fact]
    public void ResolveLinks_stops_on_links_that_point_at_each_other()
    {
        var first = Path.Combine(_root, "first");
        var second = Path.Combine(_root, "second");
        Directory.CreateSymbolicLink(first, second);
        Directory.CreateSymbolicLink(second, first);

        var resolved = TargetFolderRule.ResolveLinks(Path.Combine(first, "optimised"));

        Assert.EndsWith("optimised", resolved);
    }

    [Theory]
    [InlineData("/data/optimised", true)]
    [InlineData("/data/opti\0mised", false)]
    [InlineData("  ", false)]
    [InlineData(null, false)]
    public void IsUsablePath_refuses_text_that_cannot_be_a_path(string? path, bool usable) =>
        Assert.Equal(usable, TargetFolderRule.IsUsablePath(path));

    [Fact]
    public void ResolveLinks_keeps_parts_that_do_not_exist_yet()
    {
        var missing = Path.Combine(_root, "optimised", "not-made-yet");

        Assert.Equal(
            Path.Combine(TargetFolderRule.ResolveLinks(_root), "optimised", "not-made-yet"),
            TargetFolderRule.ResolveLinks(missing));
    }

    private string Folder(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }
}
