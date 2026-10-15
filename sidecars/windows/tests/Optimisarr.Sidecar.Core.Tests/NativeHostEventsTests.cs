using System.Text;
using System.Text.Json;
using Optimisarr.Sidecar.Core.Session;
using Optimisarr.Sidecar.Native;

namespace Optimisarr.Sidecar.Core.Tests;

public sealed class NativeHostEventsTests
{
    [Fact]
    public async Task Slow_output_keeps_bounded_progress_and_publishes_completion_last_without_secrets()
    {
        using var output = new GateStream();
        const string credential = "private-pairing-token";
        var events = new NativeHostEvents(output, () => Assert.Fail("The pipe is healthy"), credential, default);
        events.Report(new("status", Detail: credential));
        await output.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var i = 0; i < 256; i++) events.Report(new("status", Detail: new('x', 10000)));
        var finish = events.FinishAsync(new("completed", Outcome: new(1, true, credential + " delivered")));
        events.Report(new("status", Detail: "too late"));
        output.Release.SetResult();
        await finish.WaitAsync(TimeSpan.FromSeconds(5));
        var text = Encoding.UTF8.GetString(output.ToArray());
        Assert.DoesNotContain(credential, text);
        Assert.DoesNotContain("too late", text);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.InRange(lines.Length, 2, 33);
        foreach (var line in lines)
        {
            using var document = JsonDocument.Parse(line);
            Assert.Equal(1, document.RootElement.GetProperty("version").GetInt32());
            if (document.RootElement.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String) Assert.InRange(detail.GetString()!.Length, 0, 512);
        }
        using var terminal = JsonDocument.Parse(lines[^1]);
        Assert.Equal("completed", terminal.RootElement.GetProperty("type").GetString());
        Assert.Equal("[redacted] delivered", terminal.RootElement.GetProperty("outcome").GetProperty("detail").GetString());
    }

    [Fact]
    public async Task A_broken_output_pipe_cancels_owned_work_and_does_not_echo_the_underlying_secret()
    {
        using var output = new BrokenStream();
        var stopped = false;
        var events = new NativeHostEvents(output, () => stopped = true, "private-pairing-token", default);
        var error = await Assert.ThrowsAsync<IOException>(() => events.FinishAsync(new("completed", Outcome: new(1, false, "held"))));
        Assert.True(stopped);
        Assert.DoesNotContain("private-pairing-token", error.Message);
    }

    [Fact]
    public async Task Blocked_console_output_cannot_prevent_owned_work_cancellation()
    {
        using var output = new GateStream(ignoreCancellation: true);
        using var token = new CancellationTokenSource();
        var stopped = false;
        var events = new NativeHostEvents(output, () => stopped = true, null, token.Token);
        var finish = events.FinishAsync(new("completed"));
        try
        {
            await output.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            token.Cancel();
            await Assert.ThrowsAsync<IOException>(() => finish.WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.True(stopped);
        }
        finally { output.Release.TrySetResult(); try { await finish; } catch (IOException) { } }
    }

    private sealed class GateStream(bool ignoreCancellation = false) : MemoryStream
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(ignoreCancellation ? CancellationToken.None : token);
            await base.WriteAsync(buffer, token);
        }
    }
    private sealed class BrokenStream : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) =>
            ValueTask.FromException(new IOException("private-pairing-token"));
    }
}
