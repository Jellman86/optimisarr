using System.Diagnostics;
using System.Runtime.Versioning;

namespace Optimisarr.Sidecar.Service;

/// <summary>
/// Installing and removing the Windows service, driven through <c>sc.exe</c>.
///
/// <para>Done here rather than left to a separate installer so the service can be stood up on a
/// machine over SSH with nothing else present. The installer will call the same commands.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class ServiceControl
{
    public const string ServiceName = "OptimisarrSidecar";
    private const string DisplayName = "Optimisarr Sidecar";

    public static int Install()
    {
        var executable = Environment.ProcessPath;
        if (executable is null)
        {
            Console.Error.WriteLine("Could not determine this executable's path.");
            return 1;
        }

        // LocalSystem: the whole point is that it runs with nobody logged in. It also matches where
        // the credential is sealed — DPAPI at machine scope — so the service can read on first start
        // what a pairing wrote earlier from an administrator's shell.
        var arguments =
            $"create {ServiceName} binPath= \"\\\"{executable}\\\"\" start= auto " +
            $"obj= LocalSystem DisplayName= \"{DisplayName}\"";

        if (Run("sc.exe", arguments) is var created && created != 0)
        {
            return created;
        }

        // Restart on failure rather than leave a machine quietly contributing nothing: three
        // attempts a minute apart, then hourly, which recovers a transient without hammering a
        // server that is genuinely gone.
        Run("sc.exe", $"failure {ServiceName} reset= 86400 actions= restart/60000/restart/60000/restart/3600000");
        Run("sc.exe", $"description {ServiceName} \"Contributes spare encoding capacity to Optimisarr.\"");

        Console.WriteLine($"Installed {ServiceName}. Start it with: sc.exe start {ServiceName}");
        return 0;
    }

    public static int Uninstall()
    {
        // Stopped first: deleting a running service leaves it marked for deletion until the process
        // exits, and the next install then fails with a confusing "already exists".
        Run("sc.exe", $"stop {ServiceName}");
        var removed = Run("sc.exe", $"delete {ServiceName}");
        if (removed == 0)
        {
            Console.WriteLine($"Removed {ServiceName}. The stored pairing is untouched.");
        }
        return removed;
    }

    private static int Run(string file, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(file, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });

        if (process is null)
        {
            Console.Error.WriteLine($"Could not run {file}.");
            return 1;
        }

        var output = process.StandardOutput.ReadToEnd().Trim();
        var error = process.StandardError.ReadToEnd().Trim();
        process.WaitForExit();

        if (process.ExitCode != 0 && error.Length > 0)
        {
            Console.Error.WriteLine(error);
        }
        else if (output.Length > 0)
        {
            Console.WriteLine(output);
        }

        return process.ExitCode;
    }
}
