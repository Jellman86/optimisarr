using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace Optimisarr.Sidecar.Core;

internal interface IChildProcess : IDisposable
{
    bool Start();
    TextReader Output { get; }
    TextReader Error { get; }
    bool HasExited { get; }
    int ExitCode { get; }
    Task WaitForExitAsync(CancellationToken cancellationToken);
    void Kill(bool entireProcessTree);
}

internal sealed class SystemChildProcess(ProcessStartInfo startInfo) : IChildProcess
{
    private readonly Process _process = new() { StartInfo = startInfo, EnableRaisingEvents = true };
    public bool Start() => _process.Start();
    public TextReader Output => _process.StandardOutput;
    public TextReader Error => _process.StandardError;
    public bool HasExited => _process.HasExited;
    public int ExitCode => _process.ExitCode;
    public Task WaitForExitAsync(CancellationToken cancellationToken) => _process.WaitForExitAsync(cancellationToken);
    public void Kill(bool entireProcessTree) => _process.Kill(entireProcessTree);
    public void Dispose() => _process.Dispose();
}

/// <summary>
/// Owns the direct child until actual exit. Descendant retirement is established separately by
/// the inherited scratch guard; a tree-kill request or direct exit cannot release that obligation.
/// </summary>
internal sealed class OwnedChildProcess(IChildProcess process, TimeSpan? drainGrace = null) : IAsyncDisposable
{
    private readonly CancellationTokenSource _reading = new();
    private readonly List<Task> _readers = [];
    private readonly TaskCompletionSource<Exception> _readerFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _started;
    private bool _readersClosed;

    public TextReader Output => process.Output;
    public TextReader Error => process.Error;
    public int ExitCode => process.ExitCode;
    public CancellationToken ReadingToken => _reading.Token;

    public bool Start(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _started = process.Start();
    }

    public Task<T> Track<T>(Task<T> reading) { Track((Task)reading); return reading; }
    public void Track(Task reading)
    {
        _readers.Add(reading);
        _ = ObserveFailureAsync(reading);
    }

    private async Task ObserveFailureAsync(Task reading)
    {
        try { await reading.ConfigureAwait(false); }
        catch (Exception error) { _readerFailure.TrySetResult(error); }
    }

    public async Task WaitForExitAsync(CancellationToken cancellationToken)
    {
        var exit = process.WaitForExitAsync(cancellationToken);
        try
        {
            if (await Task.WhenAny(exit, _readerFailure.Task).ConfigureAwait(false) == _readerFailure.Task)
                ExceptionDispatchInfo.Capture(await _readerFailure.Task.ConfigureAwait(false)).Throw();
            await exit.ConfigureAwait(false);
        }
        finally
        {
            // A reader can fail first while the independent process wait is still in flight.
            _ = ObserveCompletionAsync(exit);
        }
    }

    private static async Task ObserveCompletionAsync(Task exit)
    {
        try { await exit.ConfigureAwait(false); }
        catch (Exception) { }
    }

    public void RequestStop()
    {
        if (!_started || HasExited()) return;
        try { process.Kill(entireProcessTree: true); }
        catch (Exception error) when (error is SystemException or AggregateException)
        {
            // Tree enumeration/termination can fail before reaching the owned direct child.
            try { process.Kill(entireProcessTree: false); }
            catch (Exception fallback) when (fallback is SystemException or AggregateException) { }
        }
    }

    private bool HasExited()
    {
        try { return process.HasExited; }
        catch (SystemException) { return false; }
    }

    public async Task<bool> DrainAsync()
    {
        var reading = Task.WhenAll(_readers);
        _ = ObserveCompletionAsync(reading);
        await Task.WhenAny(reading, Task.Delay(drainGrace ?? ChildOutput.DrainGrace)).ConfigureAwait(false);
        // WhenAll is still pending when the other pipe is inherited. Inspect each completed
        // reader so its failure cannot be hidden behind that unrelated writer.
        foreach (var reader in _readers)
            if (reader.IsCompleted) await reader.ConfigureAwait(false);
        var complete = _readers.All(reader => reader.IsCompletedSuccessfully);
        CloseReaders();
        return complete;
    }

    public async ValueTask DisposeAsync()
    {
        if (_started)
        {
            // An unconfirmed stop retains the job's ownership instead of granting false capacity.
            // No caller token or elapsed deadline can turn a failed wait into retirement proof.
            while (!HasExited())
            {
                RequestStop();
                try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception error) when (error is SystemException or AggregateException) { }
                if (!HasExited()) await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
            }
        }
        CloseReaders();
        _reading.Dispose();
        process.Dispose();
    }

    private void CloseReaders()
    {
        if (_readersClosed) return;
        _readersClosed = true;
        _reading.Cancel();
        if (_started)
        {
            Close(Output);
            Close(Error);
        }
        // Each task already has an observer. Closing/cancelling inherited pipes cannot leave
        // an unobserved failure, and callers must revoke advisory reporting before returning.
        static void Close(TextReader reader)
        {
            try { reader.Dispose(); }
            catch (Exception error) when (error is IOException or ObjectDisposedException) { }
        }
    }
}
