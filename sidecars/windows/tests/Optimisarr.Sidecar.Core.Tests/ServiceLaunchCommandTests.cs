using Optimisarr.Sidecar.Service;

namespace Optimisarr.Sidecar.Core.Tests;

/// <summary>
/// What Windows is told to run.
///
/// <para>There is no executable of our own to point at: Smart App Control judges one by reputation,
/// a freshly built unsigned apphost has none, and the service is refused with only <c>%%4551</c> in
/// the system log to say so. Every rebuild makes a new unknown file, so it never settles. The
/// service runs through <c>dotnet</c> instead, which is Microsoft-signed.</para>
///
/// <para>Getting this wrong does not fail loudly — it registers a service that cannot start, and
/// leaves a machine looking installed while contributing nothing. That is the failure mode this
/// sidecar has spent a day learning to refuse.</para>
///
/// <para>Paths are built with <see cref="Path.Combine(string, string)"/> rather than written out,
/// so the suite means the same thing on the machine that runs it as on the machine that ships it.
/// What is asserted is the decision — which runtime, and that both halves are quoted — not the
/// spelling of a separator.</para>
/// </summary>
public sealed class ServiceLaunchCommandTests
{
    private const string Assembly = "Optimisarr.Sidecar.Service.dll";

    private static readonly string InstallDirectory = Path.Combine("C:", "OptimisarrSidecar");
    private static readonly string ProgramFiles = Path.Combine("C:", "Program Files");
    private static readonly string DefaultRuntime = Path.Combine(ProgramFiles, "dotnet", "dotnet.exe");
    private static readonly string InstalledAssembly = Path.Combine(InstallDirectory, Assembly);

    private static string? Launch(
        string? processPath = null,
        string? dotnetRoot = null,
        IEnumerable<string>? present = null,
        TextWriter? errors = null) =>
        ServiceControl.LaunchCommand(
            InstallDirectory,
            processPath,
            dotnetRoot,
            ProgramFiles,
            path => (present ?? [InstalledAssembly, DefaultRuntime]).Contains(path, StringComparer.OrdinalIgnoreCase),
            errors ?? TextWriter.Null);

    [Fact]
    public void Windows_is_told_to_run_dotnet_with_this_assembly()
    {
        Assert.Equal($"\"{DefaultRuntime}\" \"{InstalledAssembly}\"", Launch());
    }

    [Fact]
    public void Both_halves_are_quoted_because_both_have_spaces_in_them_by_default()
    {
        // "C:\Program Files\dotnet\dotnet.exe" unquoted is a service that tries to run
        // C:\Program.exe with "Files\dotnet\dotnet.exe" as an argument. Windows registers that
        // without complaint and it fails at start time, which is the worst place to find out.
        var launch = Launch()!;

        Assert.StartsWith("\"", launch, StringComparison.Ordinal);
        Assert.EndsWith("\"", launch, StringComparison.Ordinal);
        Assert.Equal(4, launch.Count(character => character == '"'));
    }

    [Fact]
    public void The_runtime_that_got_this_far_is_preferred_over_any_other()
    {
        // With no apphost the running process *is* dotnet, so it is the one runtime known to work
        // with this build. A machine with two installed should not have the installer pick the
        // other one.
        var preview = Path.Combine("D:", "dotnet-preview", "dotnet.exe");

        var launch = Launch(processPath: preview, present: [InstalledAssembly, DefaultRuntime]);

        Assert.StartsWith($"\"{preview}\"", launch, StringComparison.Ordinal);
    }

    [Fact]
    public void A_machine_that_keeps_its_runtime_elsewhere_is_believed()
    {
        var root = Path.Combine("D:", "runtimes", "dotnet");
        var elsewhere = Path.Combine(root, "dotnet.exe");

        var launch = Launch(dotnetRoot: root, present: [InstalledAssembly, elsewhere]);

        Assert.StartsWith($"\"{elsewhere}\"", launch, StringComparison.Ordinal);
    }

    [Fact]
    public void No_runtime_anywhere_refuses_to_install_and_says_why()
    {
        // Rather than registering a service that cannot start. The message has to name the fix,
        // because whoever reads it is looking at a machine that did nothing at all.
        var errors = new StringWriter();

        var launch = Launch(present: [InstalledAssembly], errors: errors);

        Assert.Null(launch);
        Assert.Contains("dotnet", errors.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DOTNET_ROOT", errors.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_assembly_that_is_not_there_refuses_to_install_and_names_it()
    {
        // Installing from a directory that was never published to. Naming the path is the whole
        // message: the reader is looking at the wrong folder.
        var errors = new StringWriter();

        var launch = Launch(present: [DefaultRuntime], errors: errors);

        Assert.Null(launch);
        Assert.Contains(Assembly, errors.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_process_path_that_is_not_dotnet_is_not_mistaken_for_it()
    {
        // Under a test runner, or an older install still pointing at an apphost, the running
        // process is something else entirely and must not be registered as the runtime.
        var launch = Launch(processPath: Path.Combine(InstallDirectory, "Optimisarr.Sidecar.Service.exe"));

        Assert.StartsWith($"\"{DefaultRuntime}\"", launch, StringComparison.Ordinal);
    }
}
