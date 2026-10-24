using Optimisarr.Sidecar.Core.Capabilities;

namespace Optimisarr.Sidecar.Core.Tests;

public sealed class ProcessCommandRunnerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_incomplete_capability_listing_is_unavailable_even_with_a_zero_exit(bool inheritedError)
    {
        var held = new OwnedChildProcessTests.HeldReader();
        var child = new OwnedChildProcessTests.ControlledChild
        {
            Output = inheritedError ? new StringReader("valid-looking capability") : held,
            Error = inheritedError ? held : new StringReader("")
        };
        var operation = new ProcessCommandRunner(_ => child).RunAsync("fixture", [], CancellationToken.None);
        await child.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        child.Exit();

        var result = await operation.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal((-1, string.Empty), result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stopping_a_capability_probe_waits_for_actual_exit(bool deadline)
    {
        using var cancelled = new CancellationTokenSource();
        var child = new OwnedChildProcessTests.ControlledChild();
        var runner = new ProcessCommandRunner(_ => child,
            deadline ? TimeSpan.FromMilliseconds(20) : TimeSpan.FromSeconds(30));
        var operation = runner.RunAsync("fixture", [], cancelled.Token);
        await child.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (!deadline) cancelled.Cancel();
        await child.Stopping.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.False(operation.IsCompleted);
            Assert.False(child.Disposed);
        }
        finally { child.Exit(); }

        if (deadline) Assert.Equal((-1, string.Empty), await operation);
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.True(child.Disposed);
    }

    [Fact]
    public async Task A_cancelled_capability_probe_does_not_launch_a_process()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var absentExecutable = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ProcessCommandRunner().RunAsync(absentExecutable, [], cancelled.Token));
    }

    [Fact]
    public async Task Oversized_capability_output_is_unavailable_instead_of_retained_in_memory()
    {
        var executable = OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/sh";
        string[] arguments = OperatingSystem.IsWindows()
            ? ["-NoProfile", "-NonInteractive", "-Command", "[Console]::Out.Write(('x' * 1048577)); exit 0"]
            : ["-c", "head -c 1048577 /dev/zero | tr '\\000' x"];
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var (exitCode, output) = await new ProcessCommandRunner().RunAsync(executable, arguments, deadline.Token);

        Assert.Equal(-1, exitCode);
        Assert.Empty(output);
    }
}
