using System.Diagnostics;
using System.Runtime.Versioning;

namespace Optimisarr.Sidecar.Service;

/// <summary>
/// Installing and removing the Windows service, driven through <c>sc.exe</c>.
///
/// <para>Done here rather than left to a separate installer so the service can be stood up on a
/// machine over SSH with nothing else present. The installer will call the same commands.</para>
/// </summary>
public static class ServiceControl
{
    public const string ServiceName = "OptimisarrSidecar";

    /// <summary>The managed assembly Windows is pointed at. There is no executable of our own.</summary>
    internal const string AssemblyFileName = "Optimisarr.Sidecar.Service.dll";
    private const string DisplayName = "Optimisarr Sidecar";

    [SupportedOSPlatform("windows")]
    public static int Install()
    {
        if (LaunchCommand() is not { } launch)
        {
            return 1;
        }

        // LocalSystem: the whole point is that it runs with nobody logged in. It also matches where
        // the credential is sealed — DPAPI at machine scope — so the service can read on first start
        // what a pairing wrote earlier from an administrator's shell.
        var arguments =
            $"create {ServiceName} binPath= \"{launch.Replace("\"", "\\\"")}\" start= auto " +
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

    /// <summary>
    /// How Windows should start this service: <c>dotnet</c>, then this assembly.
    ///
    /// <para>There is no executable of our own to point at. Smart App Control is on by default on
    /// Windows 11 and judges an executable by reputation, so a freshly built unsigned apphost is
    /// refused and the service will not start — with only <c>%%4551</c> in the system log to say
    /// why, and a new unknown file on every rebuild. `dotnet` is Microsoft-signed and trusted, and
    /// the managed assembly it loads is not held to the same test.</para>
    ///
    /// <para>Null, with the reason said out loud, when no runtime can be found. Registering a
    /// service that cannot start would leave a machine looking installed and doing nothing, which
    /// is the failure this whole sidecar has spent a day learning to avoid.</para>
    /// </summary>
    internal static string? LaunchCommand() => LaunchCommand(
        AppContext.BaseDirectory,
        Environment.ProcessPath,
        Environment.GetEnvironmentVariable("DOTNET_ROOT"),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        File.Exists,
        Console.Error);

    /// <summary>
    /// The same decision with its surroundings handed in, so it can be checked on any machine
    /// rather than only on one that happens to be set up correctly.
    /// </summary>
    internal static string? LaunchCommand(
        string baseDirectory,
        string? processPath,
        string? dotnetRoot,
        string? programFiles,
        Func<string, bool> fileExists,
        TextWriter errors)
    {
        var assembly = Path.Combine(baseDirectory, AssemblyFileName);
        if (!fileExists(assembly))
        {
            errors.WriteLine($"Could not find {assembly} to install.");
            return null;
        }

        if (FindDotnet(processPath, dotnetRoot, programFiles, fileExists) is not { } dotnet)
        {
            errors.WriteLine(
                "Could not find dotnet.exe. Install the .NET runtime, or set DOTNET_ROOT to where it lives.");
            return null;
        }

        return $"\"{dotnet}\" \"{assembly}\"";
    }

    /// <summary>
    /// Where the runtime is, by the three answers that can be trusted on a service's behalf.
    ///
    /// <para>The running process first: with no apphost this <em>is</em> dotnet, so it is the same
    /// runtime that got this far. Then DOTNET_ROOT, which is how a machine says it keeps the
    /// runtime somewhere else. Then the default location.</para>
    ///
    /// <para>The PATH is deliberately not consulted. It belongs to whoever ran the install, and the
    /// service runs as LocalSystem — a runtime found only on one administrator's PATH is one the
    /// service cannot see, and registering against it would produce a machine that looks installed
    /// and does nothing.</para>
    /// </summary>
    private static string? FindDotnet(
        string? processPath, string? dotnetRoot, string? programFiles, Func<string, bool> fileExists)
    {
        if (processPath is { Length: > 0 }
            && Path.GetFileNameWithoutExtension(processPath)
                .Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return processPath;
        }

        foreach (var directory in new[] { dotnetRoot, programFiles is { Length: > 0 } ? Path.Combine(programFiles, "dotnet") : null })
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            var candidate = Path.Combine(directory, "dotnet.exe");
            if (fileExists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    [SupportedOSPlatform("windows")]
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
