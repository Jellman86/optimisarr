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
}
