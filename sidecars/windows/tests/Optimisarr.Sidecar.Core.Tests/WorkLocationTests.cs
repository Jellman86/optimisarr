using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Core.Tests;

/// <summary>
/// The work-location resolver picks a durable, usable scratch directory and never lets a bad
/// override — a blank line, a bare drive root — throw the machine back to the system drive by
/// accident or take the worker down.
/// </summary>
public sealed class WorkLocationTests
{
    [Fact]
    public void The_duration_override_file_wins_over_env_and_default()
    {
        // A path the tray (or an operator) wrote survives updates, so a reinstall can never throw
        // scratch back onto C: just because the environment variable this box happens to use differs.
        Assert.Equal(
            @"D:\OptimisarrWork",
            WorkLocation.Resolve(fromOverrideFile: @"D:\OptimisarrWork", fromEnvironment: null));
    }

    [Fact]
    public void The_environment_variable_is_the_fallback_when_there_is_no_file()
    {
        Assert.Equal(
            @"E:\scratch",
            WorkLocation.Resolve(fromOverrideFile: null, fromEnvironment: @"E:\scratch"));
    }

    [Fact]
    public void A_blank_override_file_defers_to_the_environment()
    {
        Assert.Equal(
            @"D:\work",
            WorkLocation.Resolve(fromOverrideFile: "   ", fromEnvironment: @"D:\work"));
    }

    [Fact]
    public void Neither_override_present_keeps_the_default_system_drive_path()
    {
        Assert.Equal(WorkLocation.DefaultPath, WorkLocation.Resolve(null, null));
    }

    [Fact]
    public void A_bare_drive_root_is_not_a_scratch_directory_and_is_skipped()
    {
        // "D:\" and "D:" cannot own per-job scratch folders; treat them like "not specified" and
        // move on rather than writing jobs to the root of a volume.
        Assert.Equal(
            @"D:\OptimisarrWork",
            WorkLocation.Resolve(fromOverrideFile: @"D:\", fromEnvironment: @"D:\OptimisarrWork"));
        Assert.Equal(
            WorkLocation.DefaultPath,
            WorkLocation.Resolve(fromOverrideFile: @"D:", fromEnvironment: null));
    }

    [Fact]
    public void A_trailing_separator_is_normalised_away()
    {
        Assert.Equal(
            @"E:\ram\work",
            WorkLocation.Resolve(fromOverrideFile: @"E:\ram\work\", fromEnvironment: null));
    }
}
