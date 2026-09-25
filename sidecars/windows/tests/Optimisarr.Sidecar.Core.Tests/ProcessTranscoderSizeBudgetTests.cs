using System.Diagnostics;
using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Core.Tests;

public sealed class ProcessTranscoderSizeBudgetTests
{
    [Fact]
    public async Task An_oversized_candidate_stops_the_process_and_reports_the_observed_bytes()
    {
        var root = Path.Combine(Path.GetTempPath(), "optimisarr-size-budget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var output = Path.Combine(root, "candidate.bin");
            string executable;
            string[] arguments;
            if (OperatingSystem.IsWindows())
            {
                var script = Path.Combine(root, "write-and-wait.ps1");
                await File.WriteAllTextAsync(script,
                    "$path = $args[0]\n[IO.File]::WriteAllText($path, '1234567890123')\nStart-Sleep -Seconds 120\n");
                executable = "powershell.exe";
                arguments = ["-NoProfile", "-NonInteractive", "-File", script, output];
            }
            else
            {
                executable = "/bin/sh";
                arguments = ["-c", "printf '1234567890123' > \"$1\"; sleep 120", "-", output];
            }

            var started = Stopwatch.StartNew();
            var result = await new ProcessTranscoder().RunAsync(
                executable, arguments, new Progress<double>(), CancellationToken.None,
                new OutputSizeBudget(output, 12));

            Assert.Equal(13, result.SizeBudgetExceededAtBytes);
            Assert.False(result.Succeeded);
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(15));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
