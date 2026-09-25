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

    [Fact]
    public void Selects_the_deepest_mount_that_contains_the_work_path()
    {
        var path = Path.Combine(Path.DirectorySeparatorChar.ToString(), "work", "jobs", "42");
        var root = Path.DirectorySeparatorChar.ToString();
        var work = Path.Combine(root, "work");
        var nested = Path.Combine(work, "jobs");

        var selected = WorkPaths.SelectContainingMount(path, [root, work, nested]);

        Assert.Equal(Path.TrimEndingDirectorySeparator(nested), selected);
    }

    [Fact]
    public void Work_root_itself_is_not_treated_as_owned_output()
    {
        var root = Path.GetPathRoot(_root)!;

        Assert.False(WorkPaths.IsUnderRoot(root, root));
        Assert.True(WorkPaths.IsUnderRoot(root, _root));
    }

    [Fact]
    public void Finds_only_old_unreferenced_numeric_work_directories()
    {
        var workRoot = Path.Combine(_root, "work");
        var staleOrphan = Path.Combine(workRoot, "41");
        var referenced = Path.Combine(workRoot, "42");
        var recent = Path.Combine(workRoot, "43");
        var nonJob = Path.Combine(workRoot, "preview");
        Directory.CreateDirectory(staleOrphan);
        Directory.CreateDirectory(referenced);
        Directory.CreateDirectory(recent);
        Directory.CreateDirectory(nonJob);
        var now = DateTime.UtcNow;
        Directory.SetLastWriteTimeUtc(staleOrphan, now.AddDays(-8));
        Directory.SetLastWriteTimeUtc(referenced, now.AddDays(-8));
        Directory.SetLastWriteTimeUtc(recent, now.AddHours(-1));
        Directory.SetLastWriteTimeUtc(nonJob, now.AddDays(-8));

        var result = WorkPaths.FindStaleOrphanDirectories(
            workRoot, new HashSet<int> { 42 }, now.AddDays(-7));

        Assert.Equal([staleOrphan], result);
    }

    [Fact]
    public void Leaves_a_directory_a_running_encode_has_reserved()
    {
        // Two jobs on the same media file share /work/7139. One finishes and prunes on its way out
        // while the other has created its tree but not yet opened its output — so the directory is
        // empty, but deleting it would kill the second encode with "Error opening output".
        var workRoot = Path.Combine(_root, "work");
        var mediaDir = Path.Combine(workRoot, "7139", "The Dinosaurs", "Season 1");
        Directory.CreateDirectory(mediaDir);
        var reserved = Path.GetFullPath(mediaDir);

        WorkPaths.PruneEmptyAncestors(
            workRoot,
            Path.Combine(mediaDir, "finished.mp4"),
            dir => string.Equals(dir, reserved, StringComparison.Ordinal));

        Assert.True(Directory.Exists(mediaDir));
        Assert.True(Directory.Exists(Path.Combine(workRoot, "7139")));
    }

    [Fact]
    public void Stops_walking_up_at_a_reserved_ancestor()
    {
        // The leaf is free to go, but its parent is held by another job's encode.
        var workRoot = Path.Combine(_root, "work");
        var mediaRoot = Path.Combine(workRoot, "7139");
        var leaf = Path.Combine(mediaRoot, "The Dinosaurs", "Season 1");
        Directory.CreateDirectory(leaf);
        var reserved = Path.GetFullPath(Path.Combine(mediaRoot, "The Dinosaurs"));

        WorkPaths.PruneEmptyAncestors(
            workRoot,
            Path.Combine(leaf, "gone.mp4"),
            dir => string.Equals(dir, reserved, StringComparison.Ordinal));

        Assert.False(Directory.Exists(leaf));
        Assert.True(Directory.Exists(reserved));
        Assert.True(Directory.Exists(mediaRoot));
    }

    [Fact]
    public void Prunes_as_before_when_nothing_is_reserved()
    {
        var workRoot = Path.Combine(_root, "work");
        var mediaDir = Path.Combine(workRoot, "7139", "The Dinosaurs", "Season 1");
        Directory.CreateDirectory(mediaDir);

        WorkPaths.PruneEmptyAncestors(
            workRoot, Path.Combine(mediaDir, "gone.mp4"), _ => false);

        Assert.False(Directory.Exists(Path.Combine(workRoot, "7139")));
        Assert.True(Directory.Exists(workRoot));
    }

    [Fact]
    public void Retry_recreates_the_output_directory_pruned_after_a_failed_attempt()
    {
        var workRoot = Path.Combine(_root, "work");
        var output = Path.Combine(workRoot, "42", "movie.mkv");
        var reserved = new HashSet<string>();
        IDisposable Reserve(string directory)
        {
            Assert.False(Directory.Exists(directory));
            reserved.Add(directory);
            return new Release(() => reserved.Remove(directory));
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using (WorkPaths.PrepareOutputDirectory(output, Reserve))
            {
                WorkPaths.PruneEmptyAncestors(workRoot, output, reserved.Contains);
                File.WriteAllText(output, "candidate");
                Assert.Equal("candidate", File.ReadAllText(output));
            }
            Assert.Empty(reserved);
            File.Delete(output);
            WorkPaths.PruneEmptyAncestors(workRoot, output, reserved.Contains);
            Assert.False(Directory.Exists(Path.GetDirectoryName(output)));
        }
    }

    [Fact]
    public void Output_directory_reservation_is_released_when_creation_fails()
    {
        Directory.CreateDirectory(_root);
        var parent = Path.Combine(_root, "not-a-directory");
        File.WriteAllText(parent, "keep");
        var released = false;

        Assert.ThrowsAny<IOException>(() => WorkPaths.PrepareOutputDirectory(
            Path.Combine(parent, "movie.mkv"), _ => new Release(() => released = true)));

        Assert.True(released);
        Assert.Equal("keep", File.ReadAllText(parent));
    }

    [Fact]
    public async Task Output_directory_reservation_is_released_when_an_attempt_is_cancelled()
    {
        var released = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            using var reservation = WorkPaths.PrepareOutputDirectory(
                Path.Combine(_root, "movie.mkv"), _ => new Release(() => released = true));
            await Task.FromCanceled(new CancellationToken(canceled: true));
        });
        Assert.True(released);
    }

    private sealed class Release(Action release) : IDisposable
    {
        public void Dispose() => release();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
