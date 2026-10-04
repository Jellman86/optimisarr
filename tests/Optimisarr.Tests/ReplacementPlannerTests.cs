using Optimisarr.Core.Replacement;

namespace Optimisarr.Tests;

public sealed class ReplacementPlannerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 6, 8, 17, 30, 45, 123, TimeSpan.Zero);

    private static string FixturePath(string path) => Path.Combine(
        Path.GetPathRoot(Path.GetTempPath())!, path.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public void Final_path_keeps_the_original_directory_and_name_but_takes_the_output_extension()
    {
        var plan = ReplacementPlanner.Plan(
            originalPath: FixturePath("data/films/Movie (2010)/Movie.avi"),
            workOutputPath: FixturePath("work/Movie (2010)/Movie.mkv"),
            trashRoot: FixturePath("trash"),
            nowUtc: Now,
            replacementKey: "job-42");

        Assert.Equal(FixturePath("data/films/Movie (2010)/Movie.mkv"), plan.FinalPath);
    }

    [Fact]
    public void Final_path_equals_the_original_when_the_container_is_unchanged()
    {
        var plan = ReplacementPlanner.Plan(
            FixturePath("data/films/A.mkv"), FixturePath("work/A.mkv"), FixturePath("trash"), Now, "job-42");

        Assert.Equal(FixturePath("data/films/A.mkv"), plan.FinalPath);
        Assert.Equal(FixturePath("data/films/A.mkv"), plan.OriginalPath);
    }

    [Fact]
    public void Quarantine_path_is_under_a_timestamped_folder_in_the_trash_root()
    {
        var plan = ReplacementPlanner.Plan(
            FixturePath("data/films/A.mkv"), FixturePath("work/A.mkv"), FixturePath("trash"), Now, "job-42");

        Assert.Equal(FixturePath("trash/20260608T173045123-job-42/A.mkv"), plan.QuarantinePath);
    }

    [Fact]
    public void Same_named_files_replaced_at_different_times_get_distinct_quarantine_paths()
    {
        var first = ReplacementPlanner.Plan(FixturePath("data/a/Episode.avi"), FixturePath("work/a/Episode.mkv"), FixturePath("trash"), Now, "job-1");
        var second = ReplacementPlanner.Plan(FixturePath("data/b/Episode.avi"), FixturePath("work/b/Episode.mkv"), FixturePath("trash"), Now.AddSeconds(1), "job-2");

        Assert.NotEqual(first.QuarantinePath, second.QuarantinePath);
        Assert.Equal("Episode.avi", Path.GetFileName(first.QuarantinePath));
        Assert.Equal("Episode.avi", Path.GetFileName(second.QuarantinePath));
    }

    [Fact]
    public void Same_named_files_replaced_at_the_same_time_get_distinct_quarantine_paths()
    {
        var first = ReplacementPlanner.Plan(FixturePath("data/a/Episode.avi"), FixturePath("work/a/Episode.mkv"), FixturePath("trash"), Now, "job-1");
        var second = ReplacementPlanner.Plan(FixturePath("data/b/Episode.avi"), FixturePath("work/b/Episode.mkv"), FixturePath("trash"), Now, "job-2");

        Assert.NotEqual(first.QuarantinePath, second.QuarantinePath);
    }
}
