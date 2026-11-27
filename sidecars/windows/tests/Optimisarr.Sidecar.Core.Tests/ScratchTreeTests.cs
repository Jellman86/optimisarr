using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Core.Tests;

/// <summary>
/// Removing a job's folder never follows a link out of it. A junction planted inside a job folder
/// must go as a link, leaving whatever it points at untouched.
/// </summary>
public sealed class ScratchTreeTests
{
    private sealed class FakeEntry(FakeTree tree, string path, ScratchEntryKind kind) : IScratchEntry
    {
        public ScratchEntryKind Kind => kind;

        public IEnumerable<string> Names()
        {
            tree.Listed.Add(path);
            if (kind != ScratchEntryKind.Directory) throw new InvalidOperationException($"{path} is not a directory.");
            return tree.Kinds.Keys.Where(candidate => Parent(candidate) == path).Select(candidate => candidate[(path.Length + 1)..]);
        }

        public IScratchEntry Open(string name)
        {
            var child = path + "/" + name;
            return new FakeEntry(tree, child, tree.Kinds[child]);
        }

        public void Delete()
        {
            if (kind == ScratchEntryKind.Directory && tree.Kinds.Keys.Any(candidate => Parent(candidate) == path))
                throw new IOException($"{path} is not empty.");
            tree.Kinds.Remove(path);
            tree.Deleted.Add(path);
        }

        public void Dispose() { }

        private static string Parent(string candidate) => candidate[..Math.Max(0, candidate.LastIndexOf('/'))];
    }

    private sealed class FakeTree
    {
        public readonly Dictionary<string, ScratchEntryKind> Kinds = [];
        public readonly List<string> Listed = [];
        public readonly List<string> Deleted = [];
        public IScratchEntry Open(string path) => new FakeEntry(this, path, Kinds[path]);
    }

    [Fact]
    public void A_job_folder_and_everything_in_it_is_removed()
    {
        var tree = new FakeTree();
        tree.Kinds["job"] = ScratchEntryKind.Directory;
        tree.Kinds["job/source"] = ScratchEntryKind.File;
        tree.Kinds["job/logs"] = ScratchEntryKind.Directory;
        tree.Kinds["job/logs/vmaf.json"] = ScratchEntryKind.File;

        using (var job = tree.Open("job")) ScratchTree.Delete(job);

        Assert.Empty(tree.Kinds);
        Assert.Equal("job", tree.Deleted[^1]);
    }

    [Fact]
    public void A_link_inside_a_job_folder_is_removed_without_descending_into_it()
    {
        var tree = new FakeTree();
        tree.Kinds["job"] = ScratchEntryKind.Directory;
        tree.Kinds["job/elsewhere"] = ScratchEntryKind.Link;
        // What the junction points at. The fake never resolves links, so if deletion tried to list
        // the link as a directory it would throw rather than reach these.
        tree.Kinds["outside"] = ScratchEntryKind.Directory;
        tree.Kinds["outside/precious"] = ScratchEntryKind.File;

        using (var job = tree.Open("job")) ScratchTree.Delete(job);

        Assert.DoesNotContain("job/elsewhere", tree.Listed);
        Assert.Contains("job/elsewhere", tree.Deleted);
        Assert.True(tree.Kinds.ContainsKey("outside/precious"));
    }

    [Fact]
    public void A_job_folder_that_is_itself_a_link_loses_only_the_link()
    {
        var tree = new FakeTree();
        tree.Kinds["job"] = ScratchEntryKind.Link;
        tree.Kinds["outside"] = ScratchEntryKind.Directory;
        tree.Kinds["outside/precious"] = ScratchEntryKind.File;

        using (var job = tree.Open("job")) ScratchTree.Delete(job);

        Assert.Equal(["job"], tree.Deleted);
        Assert.Empty(tree.Listed);
    }

    [Fact]
    public void A_tree_deeper_than_any_job_writes_is_refused_rather_than_walked()
    {
        var tree = new FakeTree();
        var path = "job";
        tree.Kinds[path] = ScratchEntryKind.Directory;
        for (var depth = 0; depth < ScratchTree.MaximumDepth + 1; depth++)
        {
            path += "/d";
            tree.Kinds[path] = ScratchEntryKind.Directory;
        }

        using var job = tree.Open("job");
        Assert.Throws<IOException>(() => ScratchTree.Delete(job));
    }

    [LinuxStorageFact]
    public void The_portable_removal_leaves_a_symbolic_links_target_alone()
    {
        var root = Directory.CreateTempSubdirectory("optimisarr-scratch-tree-").FullName;
        try
        {
            var outside = Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;
            File.WriteAllText(Path.Combine(outside, "precious"), "keep");
            var job = Path.Combine(root, "job-5");
            Assert.Null(JobScratch.Create(job));
            Directory.CreateSymbolicLink(Path.Combine(job, "elsewhere"), outside);
            File.WriteAllText(Path.Combine(job, "source"), "scratch");

            JobScratch.Delete(job);

            Assert.False(Directory.Exists(job));
            Assert.True(File.Exists(Path.Combine(outside, "precious")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
