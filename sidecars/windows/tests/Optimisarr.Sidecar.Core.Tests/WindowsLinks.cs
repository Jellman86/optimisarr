using System.Diagnostics;

namespace Optimisarr.Sidecar.Core.Tests;

/// <summary>Links an ordinary, unelevated account can make, as an attacker on the machine would.</summary>
internal static class WindowsLinks
{
    /// <summary>A directory junction: needs no privilege, unlike a symbolic link.</summary>
    public static void Junction(string link, string target) => MakeLink("/J", link, target);

    /// <summary>A second name for an existing file on the same volume.</summary>
    public static void HardLink(string link, string target) => MakeLink("/H", link, target);

    private static void MakeLink(string kind, string link, string target)
    {
        var start = new ProcessStartInfo("cmd.exe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "/c", "mklink", kind, link, target }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"mklink {kind} failed: {output}");
    }
}
