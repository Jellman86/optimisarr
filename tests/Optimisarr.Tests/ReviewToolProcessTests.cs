using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Optimisarr.Core.Tools;
namespace Optimisarr.Tests;

public sealed class ReviewToolProcessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Probe_rejects_excess_output_and_enforces_its_own_deadline(bool excessOutput)
    {
        var root = Path.Combine(Path.GetTempPath(), "optimisarr-bounded-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var windows = OperatingSystem.IsWindows();
            var script = Path.Combine(root, windows ? "probe.ps1" : "probe.py");
            var content = windows
                ? excessOutput ? "[Console]::Out.Write([string]::new('x', 3000000))" : "Start-Sleep -Seconds 60"
                : excessOutput ? "import os; os.write(1,b'x'*3000000)" : "import time; time.sleep(60)";
            await File.WriteAllTextAsync(script, content);
            var arguments = windows ? new[] { "-NoProfile", "-NonInteractive", "-File", script } : new[] { script };
            var result = await BoundedToolProcess.RunAsync(windows ? "powershell.exe" : "/usr/bin/python3",
                arguments, CancellationToken.None, TimeSpan.FromSeconds(excessOutput ? 15 : 0.2));
            Assert.Equal(-1, result.ExitCode);
            Assert.Empty(result.Output);
            Assert.Contains(excessOutput ? "bounded capture" : "timed out", result.Error);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReviewRegression_detection_cancellation_reaps_owned_child(bool floodStderr)
    {
        var root = Path.Combine(Path.GetTempPath(), "optimisarr-review-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var pidPath = Path.Combine(root, "pid");
        var windows = OperatingSystem.IsWindows();
        var tool = Path.Combine(root, windows ? "tool.ps1" : "tool");
        string command;
        string[] arguments;
        if (windows)
        {
            // No execution-policy override: use the host's normal test environment policy.
            var escaped = pidPath.Replace("'", "''");
            var script = $"[IO.File]::WriteAllText('{escaped}', $PID.ToString())\n";
            if (floodStderr) script += "[Console]::Error.Write([string]::new('x', 1000000))\n";
            script += "[Console]::Out.WriteLine('ffmpeg version review')\n";
            if (!floodStderr) script += "Start-Sleep -Seconds 60\n";
            File.WriteAllText(tool, script);
            command = "powershell.exe";
            arguments = ["-NoProfile", "-NonInteractive", "-File", tool];
        }
        else
        {
            var script = "#!/usr/bin/python3\nimport os,time\nopen(" + JsonSerializer.Serialize(pidPath) + ", 'w').write(str(os.getpid()))\n";
            if (floodStderr) script += "os.write(2,b'x'*1000000)\n";
            script += "print('ffmpeg version review',flush=True)\n";
            if (!floodStderr) script += "time.sleep(60)\n";
            File.WriteAllText(tool, script);
            File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            command = tool;
            arguments = [];
        }
        Process? child = null;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var run = typeof(ToolDetectionService).GetMethod("RunAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
            var task = (Task<ToolCheckResult>)run.Invoke(null, ["Review", command, false, arguments, (Func<string, ToolCheckResult>)(s => new("Review", tool, true, false, s, null)), cts.Token])!;
            var error = await Record.ExceptionAsync(async () => await task);
            if (floodStderr) Assert.Null(error);
            else Assert.IsAssignableFrom<OperationCanceledException>(error);
            try { child = Process.GetProcessById(int.Parse(File.ReadAllText(pidPath))); }
            catch (ArgumentException) { }
            if (!floodStderr) Assert.True(child is null || child.HasExited);
        }
        finally
        {
            if (child is not null) { if (!child.HasExited) { child.Kill(true); await child.WaitForExitAsync(); } child.Dispose(); }
            Directory.Delete(root, true);
        }
    }
}
