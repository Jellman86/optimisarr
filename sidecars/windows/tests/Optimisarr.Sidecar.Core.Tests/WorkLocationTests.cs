using Optimisarr.Sidecar.Core.Capabilities;
using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Core.Tests;

/// <summary>
/// The work-location resolver picks a durable, usable scratch directory, and a configured location
/// that cannot be used stops work rather than quietly sending it back to the system drive.
/// </summary>
public sealed class WorkLocationTests
{
    private const string System = "S-1-5-18";
    private const string Administrators = "S-1-5-32-544";
    private const string Users = "S-1-5-32-545";
    private const uint FullControl = 0x1F01FF;
    private const uint ReadAndExecute = 0x1200A9;

    [Fact]
    public void The_durable_override_file_wins_over_env_and_default()
    {
        var choice = WorkLocation.Resolve(fromOverrideFile: @"D:\OptimisarrWork", fromEnvironment: @"E:\scratch");

        Assert.Equal(@"D:\OptimisarrWork", choice.Path);
        Assert.Equal(WorkLocationSource.OverrideFile, choice.Source);
        Assert.Null(choice.Problem);
    }

    [Fact]
    public void The_environment_variable_is_the_fallback_when_there_is_no_file()
    {
        var choice = WorkLocation.Resolve(fromOverrideFile: null, fromEnvironment: @"E:\scratch");

        Assert.Equal(@"E:\scratch", choice.Path);
        Assert.Equal(WorkLocationSource.Environment, choice.Source);
    }

    [Fact]
    public void A_blank_override_file_defers_to_the_environment()
    {
        Assert.Equal(@"D:\work", WorkLocation.Resolve(fromOverrideFile: "  \r\n ", fromEnvironment: @"D:\work").Path);
    }

    [Fact]
    public void Neither_override_present_keeps_the_default_so_existing_installs_do_not_move()
    {
        var choice = WorkLocation.Resolve(null, null);

        Assert.Equal(WorkLocation.DefaultPath, choice.Path);
        Assert.Equal(WorkLocationSource.Default, choice.Source);
    }

    [Theory]
    [InlineData(@"D:\")]
    [InlineData(@"D:")]
    [InlineData(@"D:/")]
    [InlineData(@"D:\\")]
    public void A_bare_drive_root_in_the_file_stops_work_instead_of_falling_back(string root)
    {
        // Jobs written to the root of a volume would sit beside whatever else lives there; and
        // falling back to C: would fill the drive the operator moved scratch away from.
        var choice = WorkLocation.Resolve(fromOverrideFile: root, fromEnvironment: @"E:\OptimisarrWork");

        Assert.False(choice.Usable);
        Assert.Null(choice.Path);
        Assert.Equal(WorkLocationSource.OverrideFile, choice.Source);
        Assert.Contains("work-location file", choice.Problem);
    }

    [Fact]
    public void An_unusable_environment_variable_stops_work_instead_of_using_the_default()
    {
        var choice = WorkLocation.Resolve(fromOverrideFile: null, fromEnvironment: "scratch");

        Assert.Null(choice.Path);
        Assert.Equal(WorkLocationSource.Environment, choice.Source);
        Assert.Contains(WorkLocation.EnvironmentVariable, choice.Problem);
    }

    [Fact]
    public void An_override_file_that_cannot_be_read_or_trusted_stops_work()
    {
        var choice = WorkLocation.Resolve(@"D:\work", @"E:\work", overrideFileProblem: "unreadable");

        Assert.Null(choice.Path);
        Assert.Equal("unreadable", choice.Problem);
    }

    [Theory]
    [InlineData(@"E:\ram\work\", @"E:\ram\work")]
    [InlineData(@"e:/ram//work", @"E:\ram\work")]
    [InlineData("  R:\\OptimisarrWork  ", @"R:\OptimisarrWork")]
    public void A_usable_path_is_normalised(string configured, string expected)
    {
        Assert.Equal(expected, WorkLocation.Resolve(configured, null).Path);
    }

    [Theory]
    [InlineData(@"\\nas\share\work", "network share")]
    [InlineData(@"//nas/share/work", "network share")]
    [InlineData(@"\\?\D:\work", "network share")]
    [InlineData(@"\work", "not a full path")]
    [InlineData(@"work", "not a full path")]
    [InlineData(@"D:work", "not a full path")]
    [InlineData(@"1:\work", "not a full path")]
    [InlineData("\"D:\\Optimisarr Work\"", "quoted")]
    [InlineData(@"D:\work|now", "characters")]
    [InlineData(@"D:\work:stream", "characters")]
    [InlineData("D:\\wo\trk", "characters")]
    [InlineData("D:\\caf\uFFFD", "UTF-8")]
    [InlineData(@"D:\media\..\work", "..")]
    [InlineData(@"D:\.\work", "..")]
    [InlineData(@"D:\work.", "full stop")]
    [InlineData(@"D:\work \jobs", "space")]
    public void An_unusable_path_is_refused_with_a_reason_to_act_on(string path, string reason)
    {
        Assert.Contains(reason, WorkLocation.PathProblem(path));
    }

    [Theory]
    [InlineData(@"D:\CON")]
    [InlineData(@"D:\work\nul")]
    [InlineData(@"D:\Aux.txt\jobs")]
    [InlineData(@"D:\prn.")]
    [InlineData(@"D:\com1")]
    [InlineData(@"D:\COM9.log")]
    [InlineData(@"D:\lpt1\work")]
    [InlineData(@"D:\LPT9")]
    [InlineData("D:\\com\u00B9")]
    [InlineData(@"D:\conin$")]
    public void A_path_naming_a_device_is_refused(string path)
    {
        Assert.Contains("device", WorkLocation.PathProblem(path));
    }

    [Theory]
    [InlineData(@"D:\console")]
    [InlineData(@"D:\com10")]
    [InlineData(@"D:\lpt\work")]
    [InlineData(@"D:\nul-free")]
    public void A_folder_name_that_only_starts_like_a_device_is_usable(string path)
    {
        Assert.Null(WorkLocation.PathProblem(path));
    }

    [Fact]
    public void A_path_too_long_to_leave_room_for_job_folders_is_refused()
    {
        var longest = @"D:\" + new string('w', WorkLocation.MaximumPathLength - 3);

        Assert.Null(WorkLocation.PathProblem(longest));
        Assert.Contains("long", WorkLocation.PathProblem(longest + "w"));
    }

    [Theory]
    [InlineData(@"D:\OptimisarrWork")]
    [InlineData(@"R:\Optimisarr Work\jobs")]
    [InlineData(@"D:\work.d\scratch")]
    public void An_ordinary_local_folder_is_usable(string path)
    {
        Assert.Null(WorkLocation.PathProblem(path));
    }

    [Theory]
    [InlineData("D:\\work", "D:\\work")]
    [InlineData("D:\\work\r\n", "D:\\work")]
    [InlineData("\r\n  D:\\work  \r\n\r\n", "D:\\work")]
    public void The_override_file_holds_one_line(string text, string expected)
    {
        var (path, problem) = WorkLocation.ReadOverrideText(text);

        Assert.Equal(expected, path);
        Assert.Null(problem);
    }

    [Fact]
    public void An_override_file_with_several_lines_is_refused_rather_than_guessed()
    {
        var (path, problem) = WorkLocation.ReadOverrideText("D:\\work\r\nE:\\work\r\n");

        Assert.Null(path);
        Assert.Contains("more than one line", problem);
    }

    [Fact]
    public void An_override_file_only_administrators_and_system_control_is_trusted()
    {
        Assert.Null(WorkLocation.OverrideFileTrustProblem(Administrators, [new(System, FullControl), new(Administrators, FullControl)]));
        Assert.Null(WorkLocation.OverrideFileTrustProblem(System, [new(System, FullControl), new(Users, ReadAndExecute)]));
    }

    [Fact]
    public void An_override_file_an_ordinary_user_owns_is_not_trusted()
    {
        // A hand-made ProgramData folder lets users create files in it. The file decides where a
        // LocalSystem service creates and deletes folders, so a user-made one must not count.
        Assert.Contains("not owned", WorkLocation.OverrideFileTrustProblem("S-1-5-21-1-2-3-1001", [new(System, FullControl)]));
        Assert.Contains("not owned", WorkLocation.OverrideFileTrustProblem(null, []));
    }

    [Theory]
    [InlineData(0x1301BFu)]   // modify
    [InlineData(0x100u)]      // write attributes only
    [InlineData(0x40000000u)] // generic write
    public void An_override_file_an_ordinary_user_can_change_is_not_trusted(uint rights)
    {
        Assert.Contains("can be changed", WorkLocation.OverrideFileTrustProblem(Administrators, [new(System, FullControl), new(Users, rights)]));
    }

    [Theory]
    [InlineData(false, false, 1u, null)]
    [InlineData(true, false, 1u, "link")]
    [InlineData(false, true, 1u, "folder")]
    [InlineData(false, false, 2u, "hard link")]
    public void Only_an_ordinary_singly_linked_override_file_is_read(bool isLink, bool isDirectory, uint links, string? problem)
    {
        var found = WorkLocation.OverrideFileShapeProblem(isLink, isDirectory, links);

        if (problem is null) Assert.Null(found);
        else Assert.Contains(problem, found);
    }

    [Fact]
    public void An_override_file_saved_as_utf16_reads_as_its_path()
    {
        // Notepad's "Unicode" and Windows PowerShell 5's Out-File both write UTF-16 with a mark.
        var bytes = global::System.Text.Encoding.Unicode.GetPreamble().Concat(global::System.Text.Encoding.Unicode.GetBytes("R:\\Arbeit\u00E9\r\n")).ToArray();

        Assert.Equal(("R:\\Arbeit\u00E9", null), WorkLocation.ReadOverrideBytes(bytes));
    }

    [Fact]
    public void An_override_file_saved_as_utf8_with_or_without_a_mark_reads_as_its_path()
    {
        var plain = global::System.Text.Encoding.UTF8.GetBytes("D:\\OptimisarrWork\n");
        var marked = global::System.Text.Encoding.UTF8.GetPreamble().Concat(plain).ToArray();

        Assert.Equal((@"D:\OptimisarrWork", null), WorkLocation.ReadOverrideBytes(plain));
        Assert.Equal((@"D:\OptimisarrWork", null), WorkLocation.ReadOverrideBytes(marked));
    }

    [Fact]
    public void The_start_up_line_names_the_location_its_source_and_its_storage()
    {
        var ram = WorkLocation.Describe(new(@"R:\OptimisarrWork", WorkLocationSource.OverrideFile, null), memoryBacked: true);
        var disk = WorkLocation.Describe(new(@"D:\OptimisarrWork", WorkLocationSource.Environment, null), memoryBacked: false);
        var refused = WorkLocation.Describe(new(null, WorkLocationSource.OverrideFile, "Bad path."), memoryBacked: null);

        Assert.Contains(@"R:\OptimisarrWork", ram);
        Assert.Contains("work-location file", ram);
        Assert.Contains("RAM disk", ram);
        Assert.Contains(WorkLocation.EnvironmentVariable, disk);
        Assert.Contains("own size", disk);
        Assert.Contains("Bad path.", refused);
        Assert.Contains("takes no jobs", refused);
    }

    [Fact]
    public void The_start_up_line_says_why_a_refused_work_folder_takes_no_jobs()
    {
        var line = WorkLocation.Describe(new(@"D:\OptimisarrWork", WorkLocationSource.OverrideFile, null), memoryBacked: false,
            workFolderProblem: "It is owned by somebody else.");

        Assert.Contains(@"D:\OptimisarrWork", line);
        Assert.Contains("owned by somebody else", line);
        Assert.Contains("takes no jobs", line);
    }

    [Fact]
    public void A_worker_that_cannot_use_its_work_folder_takes_no_jobs_at_all()
    {
        var proved = SidecarCapabilities.Nothing("box") with { FreeScratchBytes = 500, MaxConcurrency = 2 };

        var reported = NativeWorkerResources.Refused(proved);

        Assert.Equal(0, reported.FreeScratchBytes);
        Assert.Equal(0, reported.MaxConcurrency);
    }

    [Fact]
    public void A_worker_without_a_work_location_reports_no_room_for_any_job()
    {
        var proved = SidecarCapabilities.Nothing("box") with
        {
            FreeScratchBytes = 500L * 1024 * 1024 * 1024,
            MaxConcurrency = 2,
            Perceptual = new Optimisarr.Core.Workers.WorkerPerceptualCapabilities { AvailableMemoryBytes = 1, ScratchUsesMemory = true },
        };

        var reported = NativeWorkerResources.Unavailable(proved);

        Assert.Equal(0, reported.FreeScratchBytes);
        Assert.Null(reported.Perceptual!.AvailableMemoryBytes);
        Assert.Null(reported.Perceptual.ScratchUsesMemory);
    }
}
