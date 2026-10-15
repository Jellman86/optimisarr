using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Core.Tests;

public sealed class ProcessTranscoderProgressTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Progress_output_larger_than_the_pipe_is_drained_even_without_a_subscriber(bool observeProgress)
    {
        string executable;
        string[] arguments;
        if (OperatingSystem.IsWindows())
        {
            executable = "powershell.exe";
            arguments = ["-NoProfile", "-NonInteractive", "-Command",
                "for ($i=0; $i -lt 16384; $i++) { [Console]::Out.WriteLine('out_time_us=1000000'); [Console]::Out.WriteLine('progress=continue') }; "
                + "[Console]::Error.WriteLine('measurement fixture finished'); exit 7; # Ignore appended FFmpeg arguments"];
        }
        else
        {
            executable = "/bin/sh";
            arguments = ["-c", "i=0; while [ $i -lt 16384 ]; do printf 'out_time_us=1000000\\nprogress=continue\\n'; i=$((i + 1)); done; "
                + "printf 'measurement fixture finished\\n' >&2; exit 7"];
        }

        // Bound a blocked producer too: the regression must not leave its child behind.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var observer = observeProgress ? new RecordingProgress() : null;
        var result = await new ProcessTranscoder().RunAsync(executable, arguments, observer, deadline.Token);

        Assert.Equal(7, result.ExitCode);
        Assert.False(result.Succeeded);
        Assert.Contains("measurement fixture finished", result.ErrorTail);
        if (observer is not null)
        {
            Assert.Equal(16384, observer.Count);
            Assert.Equal(1d, observer.LastSeconds);
        }
    }

    private sealed class RecordingProgress : IProgress<double>
    {
        public int Count { get; private set; }
        public double LastSeconds { get; private set; }

        public void Report(double value)
        {
            Count++;
            LastSeconds = value;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_cancelled_operation_does_not_launch_a_process(bool probe)
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var transcoder = new ProcessTranscoder();
        var absentExecutable = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (probe) await transcoder.ProbeAsync(absentExecutable, [], cancelled.Token);
            else await transcoder.RunAsync(absentExecutable, [], null, cancelled.Token);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_throwing_progress_observer_does_not_stop_draining_the_producer(bool ioFailure)
    {
        var (executable, arguments) = OutputFixture(
            "for ($i=0; $i -lt 16384; $i++) { [Console]::Out.WriteLine('out_time_us=1000000') }; exit 7",
            "i=0; while [ $i -lt 16384 ]; do printf 'out_time_us=1000000\\n'; i=$((i + 1)); done; exit 7");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var observer = new ThrowingProgress(ioFailure);

        var result = await new ProcessTranscoder().RunAsync(executable, arguments, observer, deadline.Token);

        Assert.Equal(7, result.ExitCode);
        Assert.Equal(1, observer.Count);
    }

    [Fact]
    public async Task Oversized_probe_evidence_is_refused_instead_of_returning_a_truncated_success()
    {
        var (executable, arguments) = OutputFixture(
            "[Console]::Out.Write(('x' * 1048577)); exit 0",
            "head -c 1048577 /dev/zero | tr '\\000' x");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ProcessTranscoder().ProbeAsync(executable, arguments, deadline.Token));

        Assert.Contains("1 MiB", error.Message);
    }

    [Fact]
    public async Task An_unbounded_stderr_line_does_not_become_an_unbounded_error_report()
    {
        var (executable, arguments) = OutputFixture(
            "[Console]::Error.Write(('x' * 1048577)); exit 7",
            "head -c 1048577 /dev/zero | tr '\\000' x >&2; exit 7");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await new ProcessTranscoder().RunAsync(executable, arguments, null, deadline.Token);

        Assert.Equal(7, result.ExitCode);
        Assert.InRange(result.ErrorTail.Length, 1, 8192);
        Assert.Contains("truncated", result.ErrorTail);
    }

    private static (string Executable, string[] Arguments) OutputFixture(string windows, string unix) =>
        OperatingSystem.IsWindows()
            ? ("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", windows + "; # Ignore appended FFmpeg arguments"])
            : ("/bin/sh", ["-c", unix]);

    private sealed class ThrowingProgress(bool ioFailure) : IProgress<double>
    {
        public int Count { get; private set; }
        public void Report(double value)
        {
            Count++;
            if (ioFailure) throw new IOException("observer unavailable");
            throw new InvalidOperationException("observer unavailable");
        }
    }
}
