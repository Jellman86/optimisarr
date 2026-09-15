using System.Diagnostics;

namespace Optimisarr.Sidecar.Core.Capabilities;

/// <summary>
/// Runs a real process.
///
/// Output is read asynchronously rather than by blocking on a stream, and stdout and stderr are
/// both drained: a child that fills a pipe nobody is reading simply stops, and the caller waits for
/// an exit that never comes. The deadline exists for the same reason — a probe that hangs would
/// hold up the service start, and on the macOS sidecar an equivalent hang held a worker's only job
/// slot until the app was killed.
/// </summary>
public sealed class ProcessCommandRunner(TimeSpan? timeout = null) : ICommandRunner
{
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromMinutes(2);

    public async Task<(int ExitCode, string Output)> RunAsync(
        string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo.FileName = executable;
        foreach (var argument in arguments)
        {
            // ArgumentList, never a joined string: a path with a space or a quote in it must not be
            // able to become a second argument, or anything else.
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return (-1, string.Empty);
        }

        // FFmpeg writes its listings to stdout and almost everything else to stderr, so a probe's
        // reason for failing is usually in the latter. Both are returned together.
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already gone */ }
            return (-1, string.Empty);
        }

        // The deadline above covers a process that will not exit. This covers a pipe that will
        // not end after it has: the listing is worth two seconds, never a stalled service start.
        return (
            process.ExitCode,
            await ChildOutput.WithinGraceAsync(stdout) + "\n" + await ChildOutput.WithinGraceAsync(stderr));
    }
}
