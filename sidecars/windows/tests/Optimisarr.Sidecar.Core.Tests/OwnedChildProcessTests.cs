using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Core.Tests;

public sealed class OwnedChildProcessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_retains_ownership_until_the_direct_child_has_actually_exited(bool probe)
    {
        using var cancelled = new CancellationTokenSource();
        var child = new ControlledChild();
        var transcoder = new ProcessTranscoder(_ => child);
        var operation = probe ? RunProbe() : RunEncode();
        await child.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancelled.Cancel();
        await child.Stopping.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.False(operation.IsCompleted);
            Assert.False(child.Disposed);
        }
        finally { child.Exit(); }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.True(child.Disposed);
        Assert.True(child.IndependentExitWait);

        async Task RunProbe() => await transcoder.ProbeAsync("fixture", [], cancelled.Token);
        async Task RunEncode() => await transcoder.RunAsync("fixture", [], null, cancelled.Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_reader_failure_stops_the_writer_without_waiting_for_the_other_pipe(bool probe)
    {
        var heldPipe = new HeldReader();
        var child = new ControlledChild { Output = new FailedReader(), Error = heldPipe };
        var transcoder = new ProcessTranscoder(_ => child, TimeSpan.FromMilliseconds(20));
        var operation = probe ? RunProbe() : RunEncode();
        await child.Stopping.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { Assert.False(operation.IsCompleted); }
        finally { child.Exit(); }

        await Assert.ThrowsAsync<IOException>(() => operation);
        Assert.True(child.Disposed);
        Assert.True(heldPipe.Disposed);

        async Task RunProbe() => await transcoder.ProbeAsync("fixture", [], CancellationToken.None);
        async Task RunEncode() => await transcoder.RunAsync("fixture", [], null, CancellationToken.None);
    }

    [Fact]
    public async Task A_failed_tree_kill_still_stops_and_reaps_the_direct_child()
    {
        using var cancelled = new CancellationTokenSource();
        var child = new ControlledChild { FailTreeKill = true };
        var operation = new ProcessTranscoder(_ => child).RunAsync("fixture", [], null, cancelled.Token);
        await child.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancelled.Cancel();
        await child.Stopping.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.True(child.DirectKillRequested);
            Assert.False(operation.IsCompleted);
        }
        finally { child.Exit(); }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.True(child.Disposed);
    }

    [Fact]
    public async Task A_failed_exit_wait_is_not_treated_as_proof_of_exit()
    {
        var child = new ControlledChild { FailFirstWait = true };
        var operation = new ProcessTranscoder(_ => child).RunAsync("fixture", [], null, CancellationToken.None);
        await child.Stopping.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.False(operation.IsCompleted);
            Assert.False(child.Disposed);
        }
        finally { child.Exit(); }

        await Assert.ThrowsAsync<IOException>(() => operation);
        Assert.True(child.Disposed);
        Assert.True(child.IndependentExitWait);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_exited_parent_does_not_wait_forever_for_an_inherited_pipe(bool probe)
    {
        var output = new HeldReader();
        var error = new HeldReader();
        var child = new ControlledChild { Output = output, Error = error };
        var transcoder = new ProcessTranscoder(_ => child, TimeSpan.FromMilliseconds(20));
        var operation = probe ? RunProbe() : RunEncode();
        await child.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        child.Exit();

        await operation.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(output.Disposed);
        Assert.True(error.Disposed);
        await output.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await error.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(child.Disposed);

        async Task RunProbe() => await transcoder.ProbeAsync("fixture", [], CancellationToken.None);
        async Task RunEncode() => await transcoder.RunAsync("fixture", [], null, CancellationToken.None);
    }

    [Fact]
    public async Task An_incomplete_probe_is_not_successful_evidence_even_when_the_child_exited_zero()
    {
        var child = new ControlledChild { Output = new HeldReader() };
        var operation = new ProcessTranscoder(_ => child, TimeSpan.FromMilliseconds(20))
            .ProbeAsync("fixture", [], CancellationToken.None);
        await child.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        child.Exit();

        var result = await operation.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(-1, result.ExitCode);
        Assert.Empty(result.Output);
    }

    [Fact]
    public async Task A_pipe_that_faults_during_closure_cannot_turn_into_successful_probe_evidence()
    {
        var output = new FaultOnCloseReader();
        var child = new ControlledChild { Output = output };
        var operation = new ProcessTranscoder(_ => child, TimeSpan.FromMilliseconds(20))
            .ProbeAsync("fixture", [], CancellationToken.None);
        await child.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        child.Exit();

        var result = await operation.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(-1, result.ExitCode);
        Assert.Empty(result.Output);
        await output.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(child.Disposed);
    }

    internal sealed class ControlledChild : IChildProcess
    {
        private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _waits;
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Stopping { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TextReader Output { get; init; } = new StringReader("");
        public TextReader Error { get; init; } = new StringReader("");
        public bool FailTreeKill { get; init; }
        public bool FailFirstWait { get; init; }
        public bool DirectKillRequested { get; private set; }
        public bool IndependentExitWait { get; private set; }
        public bool Disposed { get; private set; }
        public bool HasExited => _exit.Task.IsCompletedSuccessfully;
        public int ExitCode => HasExited ? 0 : throw new InvalidOperationException("still running");
        public bool Start() => true;
        public Task WaitForExitAsync(CancellationToken token)
        {
            IndependentExitWait |= !token.CanBeCanceled;
            Waiting.TrySetResult();
            if (Interlocked.Increment(ref _waits) == 1 && FailFirstWait)
                return Task.FromException(new IOException("exit wait unavailable"));
            return _exit.Task.WaitAsync(token);
        }
        public void Kill(bool entireProcessTree)
        {
            if (entireProcessTree && FailTreeKill) throw new AggregateException(new IOException("tree unavailable"));
            DirectKillRequested |= !entireProcessTree;
            Stopping.TrySetResult();
        }
        public void Exit() => _exit.TrySetResult();
        public void Dispose()
        {
            Assert.True(HasExited);
            Disposed = true;
        }
    }

    private sealed class FailedReader : TextReader
    {
        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken token = default) =>
            ValueTask.FromException<int>(new IOException("reader failed"));
    }

    internal sealed class HeldReader : TextReader
    {
        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken token = default)
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); return 0; }
            finally { Stopped.TrySetResult(); }
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class FaultOnCloseReader : TextReader
    {
        private readonly TaskCompletionSource<int> _read = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken token = default)
        {
            try { return await _read.Task; }
            finally { Stopped.TrySetResult(); }
        }
        protected override void Dispose(bool disposing)
        {
            _read.TrySetException(new IOException("pipe closed during read"));
            base.Dispose(disposing);
        }
    }
}
