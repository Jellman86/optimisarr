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
                // Inline commands work under the normal PowerShell script-file policy.
                // Encode the path as data so quotes or shell syntax cannot become code.
                var pathData = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(output));
                var command = $"$path = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{pathData}')); "
                    + "[IO.File]::WriteAllText($path, '1234567890123'); Start-Sleep -Seconds 120; # Ignore the appended FFmpeg progress arguments";
                executable = "powershell.exe";
                arguments = ["-NoProfile", "-NonInteractive", "-Command", command];
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

            Assert.True(result.SizeBudgetExceededAtBytes is not null,
                $"Fixture exited {result.ExitCode} without hitting the budget: {result.ErrorTail}");
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
