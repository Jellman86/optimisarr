using Optimisarr.Api.Queue;

namespace Optimisarr.Tests;

public sealed class WorkPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "optimisarr-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Prunes_the_empty_per_media_directory_after_its_output_is_gone()
    {
        // /work/42/Album mirrors a job's scratch tree; the output file has already been removed.
        var workRoot = Path.Combine(_root, "work");
        var mediaDir = Path.Combine(workRoot, "42", "Album");
        Directory.CreateDirectory(mediaDir);
        var outputPath = Path.Combine(mediaDir, "Track.opus"); // never created — already consumed

        WorkPaths.PruneEmptyAncestors(workRoot, outputPath);

        Assert.False(Directory.Exists(Path.Combine(workRoot, "42")));
        Assert.True(Directory.Exists(workRoot)); // the work root itself is never removed
    }

    [Fact]
    public void Stops_at_the_first_non_empty_directory()
    {
        var workRoot = Path.Combine(_root, "work");
        var mediaDir = Path.Combine(workRoot, "7", "Season 1");
        Directory.CreateDirectory(mediaDir);
        // A sibling output of another file in /work/7 keeps that directory alive.
        var sibling = Path.Combine(workRoot, "7", "keep.mkv");
        File.WriteAllText(sibling, "x");

        WorkPaths.PruneEmptyAncestors(workRoot, Path.Combine(mediaDir, "Ep.mkv"));

        Assert.False(Directory.Exists(mediaDir));            // the empty leaf is pruned
        Assert.True(Directory.Exists(Path.Combine(workRoot, "7"))); // but the non-empty parent stays
        Assert.True(File.Exists(sibling));
    }

    [Fact]
    public void Never_walks_above_the_work_root()
    {
        var workRoot = Path.Combine(_root, "work");
        Directory.CreateDirectory(workRoot);
        // A path outside the work root (e.g. a moved-to-target output) must not trigger deletion.
        var outside = Path.Combine(_root, "elsewhere", "file.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);

        WorkPaths.PruneEmptyAncestors(workRoot, outside);

        Assert.True(Directory.Exists(Path.Combine(_root, "elsewhere")));
        Assert.True(Directory.Exists(workRoot));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
