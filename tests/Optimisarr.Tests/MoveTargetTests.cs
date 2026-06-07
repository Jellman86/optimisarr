using Optimisarr.Core.Queue;

namespace Optimisarr.Tests;

public sealed class MoveTargetTests
{
    [Fact]
    public void Maps_a_work_output_to_the_same_relative_path_under_the_target()
    {
        var dest = MoveTarget.Resolve(
            workRoot: "/work",
            workOutputPath: "/work/Bluey/Season 2/Bus.mkv",
            targetFolder: "/data/testing-out");

        Assert.Equal("/data/testing-out/Bluey/Season 2/Bus.mkv", dest);
    }

    [Fact]
    public void Handles_a_file_at_the_work_root()
    {
        var dest = MoveTarget.Resolve("/work", "/work/Movie.mkv", "/out");

        Assert.Equal("/out/Movie.mkv", dest);
    }

    [Fact]
    public void Tolerates_trailing_slashes_on_the_roots()
    {
        var dest = MoveTarget.Resolve("/work/", "/work/sub/a.mkv", "/out/");

        Assert.Equal("/out/sub/a.mkv", dest);
    }
}
