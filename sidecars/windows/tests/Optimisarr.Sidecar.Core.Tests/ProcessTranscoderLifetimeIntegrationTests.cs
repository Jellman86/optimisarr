using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Core.Tests;

[Collection("Native scratch ownership")]
public sealed class ProcessTranscoderLifetimeIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "optimisarr-owned-process-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancelled_real_encode_and_probe_have_exited_before_returning(bool probe)
    {
        Directory.CreateDirectory(_root);
        var ready = Path.Combine(_root, "process-id");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var cancelled = new CancellationTokenSource();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancelled.Token);
        var (host, arguments) = Fixture("encode-wait", ready);
        var transcoder = new ProcessTranscoder();
        var operation = probe ? RunProbe() : RunEncode();
        Process? child = null;
        try
        {
            await WaitForFileAsync(ready, operation, deadline.Token);
            child = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(ready, deadline.Token), CultureInfo.InvariantCulture));
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
            Assert.True(child.HasExited);
        }
        finally
        {
            cancelled.Cancel();
            try { await operation; } catch (OperationCanceledException) { }
            if (child is not null)
            {
                if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(CancellationToken.None); }
                child.Dispose();
            }
        }
        async Task RunProbe() => await transcoder.ProbeAsync(host, arguments, lifetime.Token);
        async Task RunEncode() => await transcoder.RunAsync(host, arguments, null, lifetime.Token);
    }

    [Fact]
    public async Task An_inherited_pipe_is_bounded_while_its_surviving_scratch_writer_remains_busy()
    {
        Directory.CreateDirectory(_root);
        var work = Path.Combine(_root, "work");
        var ready = Path.Combine(_root, "holder.json");
        var test = typeof(ProcessTranscoderLifetimeIntegrationTests).Assembly.Location;
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var (_, arguments) = Fixture("pipe-parent", work, host,
            Path.ChangeExtension(test, ".runtimeconfig.json"), Path.ChangeExtension(test, ".deps.json"), ready);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var observer = new RecordingProgress();
        var operation = new ProcessTranscoder().RunAsync(host, arguments, observer, deadline.Token);
        Process? holder = null;
        string? directory = null;
        try
        {
            await WaitForFileAsync(ready, operation, deadline.Token);
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(ready, deadline.Token));
            holder = Process.GetProcessById(json.RootElement.GetProperty("childId").GetInt32());
            directory = json.RootElement.GetProperty("directory").GetString()!;
            var result = await operation.WaitAsync(TimeSpan.FromSeconds(15), deadline.Token);
            Assert.Equal(0, result.ExitCode);
            Assert.False(holder.HasExited);
            Assert.Equal(1, NativeScratchWorkspace.Recover(work).Busy);
            Assert.Equal(1, observer.Count);

            await File.WriteAllTextAsync(Path.Combine(directory, "late-progress"), "go", deadline.Token);
            await WaitForFileAsync(Path.Combine(directory, "late-attempted"), holder.WaitForExitAsync(deadline.Token), deadline.Token);
            Assert.Equal(1, observer.Count);
            Assert.Equal(1, NativeScratchWorkspace.Recover(work).Busy);

            await File.WriteAllTextAsync(Path.Combine(directory, "release"), "go", deadline.Token);
            await holder.WaitForExitAsync(deadline.Token);
            Assert.Equal(1, NativeScratchWorkspace.Recover(work).Removed);
        }
        finally
        {
            if (directory is not null && Directory.Exists(directory))
            {
                await File.WriteAllTextAsync(Path.Combine(directory, "late-progress"), "go");
                await File.WriteAllTextAsync(Path.Combine(directory, "release"), "go");
            }
            if (holder is not null)
            {
                if (!holder.HasExited) { holder.Kill(entireProcessTree: true); await holder.WaitForExitAsync(CancellationToken.None); }
                holder.Dispose();
            }
            deadline.Cancel();
            try { await operation; } catch (OperationCanceledException) { }
            NativeScratchWorkspace.Recover(work);
        }
    }

    private static (string Host, string[] Arguments) Fixture(params string[] arguments)
    {
        var test = typeof(ProcessTranscoderLifetimeIntegrationTests).Assembly.Location;
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        return (host, ["exec", "--runtimeconfig", Path.ChangeExtension(test, ".runtimeconfig.json"),
            "--depsfile", Path.ChangeExtension(test, ".deps.json"), typeof(ScratchFixture.Program).Assembly.Location, .. arguments]);
    }

    private static async Task WaitForFileAsync(string path, Task operation, CancellationToken token)
    {
        while (!File.Exists(path))
        {
            if (operation.IsCompleted) { await operation; Assert.Fail("Fixture exited before its handshake."); }
            await Task.Delay(20, token);
        }
    }

    private sealed class RecordingProgress : IProgress<double>
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public void Report(double value) => Interlocked.Increment(ref _count);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
