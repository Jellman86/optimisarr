using System.Text;
using System.Text.Json;
using Optimisarr.Sidecar.Native;

namespace Optimisarr.Sidecar.Core.Tests;

public sealed class NativeHostLifecycleTests
{
    private static byte[] Request() => JsonSerializer.SerializeToUtf8Bytes(new NativeHostRequest(1, "capabilities",
        Path.GetFullPath("ffmpeg"), Path.GetFullPath("scratch")), NativeHostProtocol.Json).Concat(new byte[] { 10 }).ToArray();

    [Fact]
    public async Task An_invalid_request_never_starts_owned_work_and_has_a_safe_refusal()
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("{private-pairing-token\n"));
        using var output = new MemoryStream();
        var result = await NativeHostProgram.RunAsync(input, output, default, (_, _, _) => throw new InvalidOperationException("Work must not start"));
        Assert.Equal(2, result);
        var text = Encoding.UTF8.GetString(output.ToArray());
        Assert.DoesNotContain("private-pairing-token", text);
        using var document = JsonDocument.Parse(text);
        Assert.Equal("refused", document.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Parent_death_cancels_and_awaits_the_owned_operation_before_returning()
    {
        using var input = new ParentStream(Request());
        using var output = new MemoryStream();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = false;
        var run = NativeHostProgram.RunAsync(input, output, default, async (_, _, token) =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { stopped = true; }
            return new("capabilities", Capabilities: new());
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        input.ParentClosed.SetResult();
        Assert.Equal(1, await run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(stopped);
    }

    [Fact]
    public async Task Successful_completion_stops_the_parent_monitor_and_publishes_one_final_result()
    {
        using var input = new ParentStream(Request());
        using var output = new MemoryStream();
        var result = await NativeHostProgram.RunAsync(input, output, default,
            (_, report, _) => { report(new("status", Detail: "qualifying")); return Task.FromResult(new NativeHostEvent("capabilities", Capabilities: new())); });
        Assert.Equal(0, result);
        Assert.True(input.MonitorCancelled);
        var lines = Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using var last = JsonDocument.Parse(lines[^1]);
        Assert.Equal("capabilities", last.RootElement.GetProperty("type").GetString());
        Assert.Single(lines, line => line.Contains("\"type\":\"capabilities\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_OS_signal_stops_the_host_even_when_console_input_ignores_cancellation()
    {
        using var input = new ParentStream(Request(), ignoreCancellation: true);
        using var output = new MemoryStream();
        using var signal = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = false;
        var run = NativeHostProgram.RunAsync(input, output, signal.Token, async (_, _, token) =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { stopped = true; }
            return new("capabilities", Capabilities: new());
        });
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            signal.Cancel();
            Assert.Equal(1, await run.WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.True(stopped);
        }
        finally { input.ParentClosed.TrySetResult(); await run; }
    }

    [Fact]
    public async Task Initial_request_wait_is_bounded_when_console_input_ignores_cancellation()
    {
        using var input = new ParentStream([], ignoreCancellation: true);
        using var signal = new CancellationTokenSource();
        var read = NativeHostProtocol.ReadRequestAsync(input, signal.Token);
        try
        {
            signal.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(1)));
        }
        finally { input.ParentClosed.TrySetResult(); try { await read; } catch (IOException) { } catch (OperationCanceledException) { } }
    }

    [Fact]
    public async Task A_recovery_result_requires_a_successful_host_exit_and_stops_parent_monitoring()
    {
        using var input = new ParentStream(Request());
        using var output = new MemoryStream();
        var result = await NativeHostProgram.RunAsync(input, output, default,
            (_, _, _) => Task.FromResult(new NativeHostEvent("recovered")));
        Assert.Equal(0, result);
        Assert.True(input.MonitorCancelled);
    }

    private sealed class ParentStream(byte[] frame, bool ignoreCancellation = false) : Stream
    {
        private int _position;
        public TaskCompletionSource ParentClosed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool MonitorCancelled { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (_position < frame.Length)
            {
                var count = Math.Min(buffer.Length, frame.Length - _position);
                frame.AsMemory(_position, count).CopyTo(buffer); _position += count; return count;
            }
            try { await ParentClosed.Task.WaitAsync(ignoreCancellation ? CancellationToken.None : token); return 0; }
            catch (OperationCanceledException) { MonitorCancelled = true; throw; }
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
