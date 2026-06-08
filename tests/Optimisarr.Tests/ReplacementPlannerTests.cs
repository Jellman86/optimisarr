using Optimisarr.Core.Replacement;

namespace Optimisarr.Tests;

public sealed class ReplacementPlannerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 6, 8, 17, 30, 45, 123, TimeSpan.Zero);

    [Fact]
    public void Final_path_keeps_the_original_directory_and_name_but_takes_the_output_extension()
    {
        var plan = ReplacementPlanner.Plan(
            originalPath: "/data/films/Movie (2010)/Movie.avi",
            workOutputPath: "/work/Movie (2010)/Movie.mkv",
            trashRoot: "/trash",
            nowUtc: Now);

        Assert.Equal("/data/films/Movie (2010)/Movie.mkv", plan.FinalPath);
    }

    [Fact]
    public void Final_path_equals_the_original_when_the_container_is_unchanged()
    {
        var plan = ReplacementPlanner.Plan(
            "/data/films/A.mkv", "/work/A.mkv", "/trash", Now);

        Assert.Equal("/data/films/A.mkv", plan.FinalPath);
        Assert.Equal("/data/films/A.mkv", plan.OriginalPath);
    }

    [Fact]
    public void Quarantine_path_is_under_a_timestamped_folder_in_the_trash_root()
    {
        var plan = ReplacementPlanner.Plan(
            "/data/films/A.mkv", "/work/A.mkv", "/trash", Now);

        Assert.Equal("/trash/20260608T173045123/A.mkv", plan.QuarantinePath);
    }

    [Fact]
    public void Same_named_files_replaced_at_different_times_get_distinct_quarantine_paths()
    {
        var first = ReplacementPlanner.Plan("/data/a/Episode.avi", "/work/a/Episode.mkv", "/trash", Now);
        var second = ReplacementPlanner.Plan("/data/b/Episode.avi", "/work/b/Episode.mkv", "/trash", Now.AddSeconds(1));

        Assert.NotEqual(first.QuarantinePath, second.QuarantinePath);
        Assert.EndsWith("/Episode.avi", first.QuarantinePath);
        Assert.EndsWith("/Episode.avi", second.QuarantinePath);
    }
}
