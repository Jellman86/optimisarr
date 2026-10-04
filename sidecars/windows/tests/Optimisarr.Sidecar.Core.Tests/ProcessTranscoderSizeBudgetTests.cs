using System.Diagnostics;
using System.Globalization;
using Optimisarr.Sidecar.Core.Session;
using Xunit.Abstractions;

namespace Optimisarr.Sidecar.Core.Tests;

public sealed class ProcessTranscoderSizeBudgetTests(ITestOutputHelper evidence)
{
    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    public async Task An_oversized_candidate_stops_the_process_and_reports_the_observed_bytes(int startupDelaySeconds)
    {
        var root = Path.Combine(Path.GetTempPath(), "optimisarr-size-budget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        Task<TranscodeResult>? encode = null;
        Process? child = null;
        var started = Stopwatch.StartNew();
        var launchedAt = DateTime.UtcNow;
        try
        {
            var output = Path.Combine(root, "candidate.bin");
            var childPid = Path.Combine(root, "child.pid");
            string executable;
            string[] arguments;
            if (OperatingSystem.IsWindows())
            {
                // Inline commands work under the normal PowerShell script-file policy.
                // Encode the path as data so quotes or shell syntax cannot become code.
                var pathData = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(root));
                var command = $"$root = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{pathData}')); "
                    + "[IO.File]::WriteAllText((Join-Path $root 'fixture-started'), 'started'); "
                    + $"Start-Sleep -Seconds {startupDelaySeconds}; "
                    + "$child = Start-Process powershell.exe -ArgumentList @('-NoProfile', '-NonInteractive', '-Command', 'Start-Sleep -Seconds 120') -NoNewWindow -PassThru; "
                    + "[IO.File]::WriteAllText((Join-Path $root 'child.pid.tmp'), [string]$child.Id); "
                    + "[IO.File]::Move((Join-Path $root 'child.pid.tmp'), (Join-Path $root 'child.pid')); "
                    + "while (!(Test-Path (Join-Path $root 'child-observed'))) { Start-Sleep -Milliseconds 25 }; "
                    + "[IO.File]::WriteAllText((Join-Path $root 'candidate.bin'), '1234567890123'); "
                    + "Wait-Process -Id $child.Id; # Ignore the appended FFmpeg progress arguments";
                executable = "powershell.exe";
                arguments = ["-NoProfile", "-NonInteractive", "-Command", command];
            }
            else
            {
                executable = "/bin/sh";
                arguments = ["-c", "printf started > \"$1/fixture-started\"; sleep \"$2\"; "
                    + "sleep 120 & child=$!; printf '%s' \"$child\" > \"$1/child.pid.tmp\"; mv \"$1/child.pid.tmp\" \"$1/child.pid\"; "
                    + "while [ ! -f \"$1/child-observed\" ]; do sleep 0.025; done; "
                    + "printf '1234567890123' > \"$1/candidate.bin\"; wait \"$child\"", "-", root,
                    startupDelaySeconds.ToString(CultureInfo.InvariantCulture)];
            }

            encode = new ProcessTranscoder().RunAsync(
                executable, arguments, new Progress<double>(), deadline.Token,
                new OutputSizeBudget(output, 12));

            while (!File.Exists(childPid))
            {
                if (encode.IsCompleted)
                {
                    var early = await encode;
                    Assert.Fail($"Fixture exited before starting its child: {early.ExitCode}: {early.ErrorTail}");
                }
                await Task.Delay(25, deadline.Token);
            }
            child = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(childPid, deadline.Token), CultureInfo.InvariantCulture));
            await File.WriteAllTextAsync(Path.Combine(root, "child-observed"), "observed", deadline.Token);
            var result = await encode;
            var finishedAt = DateTime.UtcNow;
            var crossedAt = File.GetLastWriteTimeUtc(output);
            var abortLatency = finishedAt - crossedAt;
            var phases = DescribePhases(root, launchedAt, finishedAt, started.Elapsed);

            Assert.True(result.SizeBudgetExceededAtBytes is not null,
                $"Fixture exited {result.ExitCode} without hitting the budget: {result.ErrorTail}");
            Assert.Equal(13, result.SizeBudgetExceededAtBytes);
            Assert.False(result.Succeeded);
            Assert.True(abortLatency >= TimeSpan.Zero && abortLatency < TimeSpan.FromSeconds(15), phases);
            await child.WaitForExitAsync(deadline.Token);
            Assert.True(child.HasExited, $"The sleeping child survived the size guard. {phases}");
        }
        finally
        {
            evidence.WriteLine(DescribePhases(root, launchedAt, DateTime.UtcNow, started.Elapsed));
            await deadline.CancelAsync();
            if (encode is not null)
            {
                try { await encode; }
                catch (OperationCanceledException) { }
            }
            if (child is not null)
            {
                if (!child.HasExited) child.Kill(entireProcessTree: true);
                child.Dispose();
            }
            Directory.Delete(root, recursive: true);
        }
    }

    private static string DescribePhases(string root, DateTime launchedAt, DateTime finishedAt, TimeSpan total)
    {
        var fixture = Path.Combine(root, "fixture-started");
        var candidate = Path.Combine(root, "candidate.bin");
        var beganAt = File.Exists(fixture) ? File.GetLastWriteTimeUtc(fixture) : (DateTime?)null;
        var crossedAt = File.Exists(candidate) ? File.GetLastWriteTimeUtc(candidate) : (DateTime?)null;
        return $"startup={beganAt - launchedAt}; fixture delay={crossedAt - beganAt}; "
            + $"budget crossing to return={finishedAt - crossedAt}; total={total}; "
            + $"fixture started={beganAt?.ToString("O") ?? "missing"}; budget crossed={crossedAt?.ToString("O") ?? "missing"}";
    }
}
