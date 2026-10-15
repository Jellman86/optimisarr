using System.Diagnostics;
using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Core.Capabilities;

/// <summary>Capability commands with bounded output, a deadline and confirmed direct-child exit.</summary>
public sealed class ProcessCommandRunner : ICommandRunner
{
    private readonly TimeSpan _timeout;
    private readonly Func<ProcessStartInfo, IChildProcess> _createProcess;

    public ProcessCommandRunner(TimeSpan? timeout = null)
        : this(info => new SystemChildProcess(info), timeout) { }

    internal ProcessCommandRunner(Func<ProcessStartInfo, IChildProcess> createProcess, TimeSpan? timeout = null)
    {
        _createProcess = createProcess;
        _timeout = timeout ?? TimeSpan.FromMinutes(2);
    }

    public async Task<(int ExitCode, string Output)> RunAsync(
        string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        try
        {
            await using var process = new OwnedChildProcess(_createProcess(ProcessTranscoder.StartInfo(executable, arguments)));
            if (!process.Start(deadline.Token)) return (-1, string.Empty);
            var stdout = process.Track(ChildText.ReadBoundedAsync(process.Output, process.ReadingToken));
            var stderr = process.Track(ChildText.ReadBoundedAsync(process.Error, process.ReadingToken));
            await process.WaitForExitAsync(deadline.Token);
            if (!await process.DrainAsync()) return (-1, string.Empty);
            return (process.ExitCode, ProcessTranscoder.CompletedText(stdout) + "\n" + ProcessTranscoder.CompletedText(stderr));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (-1, string.Empty);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return (-1, string.Empty);
        }
    }
}
