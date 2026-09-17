using Optimisarr.Sidecar.Core.Session;
using Optimisarr.Sidecar.Service;

namespace Optimisarr.Sidecar.Core.Tests;

public sealed class MonitorTests
{
    [Fact]
    public void Local_monitor_never_exposes_query_tokens_or_fragments() =>
        Assert.Equal("https://server.test/base", MonitorProtocol.PublicServerAddress("https://server.test/base?token=secret#private"));

    [Fact]
    public async Task Native_pipe_pause_and_resume_only_change_claiming_and_return_no_secrets()
    {
        if (!OperatingSystem.IsWindows()) return;
        var session = new SidecarSession(new SidecarClient(new HttpClient()), new InMemoryCredentialStore(),
            _ => throw new InvalidOperationException("Must not probe"), () => null, Task.Delay);
        foreach (var command in new byte[] { MonitorProtocol.Pause, MonitorProtocol.Read, MonitorProtocol.Resume, 255 })
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var name = "Optimisarr-test-" + Guid.NewGuid().ToString("N");
            using var pipe = new System.IO.Pipes.NamedPipeServerStream(name, System.IO.Pipes.PipeDirection.InOut, 1,
                System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly);
            var serve = Task.Run(async () =>
            {
                if (!OperatingSystem.IsWindows()) return;
                await pipe.WaitForConnectionAsync(timeout.Token);
                await MonitorServer.ExchangeAsync(pipe, session, () => new MonitorSnapshot("Test", "Connected", "Ready", session.IsPaused,
                    "http://test", null, null, [], null, "test"), timeout.Token);
                pipe.Disconnect();
            });
            using var client = new System.IO.Pipes.NamedPipeClientStream(".", name, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
            await client.ConnectAsync(timeout.Token);
            await client.WriteAsync(new[] { command }, timeout.Token);
            if (command == 255) { await serve; Assert.False(session.IsPaused); continue; }
            var header = new byte[4];
            await client.ReadExactlyAsync(header, timeout.Token);
            var responseBytes = new byte[BitConverter.ToInt32(header)];
            await client.ReadExactlyAsync(responseBytes, timeout.Token);
            await client.WriteAsync(new byte[] { 0 }, timeout.Token);
            await serve;
            var response = System.Text.Encoding.UTF8.GetString(responseBytes);
            Assert.DoesNotContain("Credential", response);
            var snapshot = System.Text.Json.JsonSerializer.Deserialize<MonitorSnapshot>(response)!;
            Assert.Equal(command != MonitorProtocol.Resume, snapshot.Paused);
        }
    }

    [Fact]
    public void Monitor_pipe_grants_interactive_access_but_denies_network_tokens()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var pipe = MonitorServer.CreatePipe("Optimisarr-acl-test-" + Guid.NewGuid().ToString("N"));
        var rules = System.IO.Pipes.PipesAclExtensions.GetAccessControl(pipe).GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier));
        var entries = rules.Cast<System.IO.Pipes.PipeAccessRule>().ToArray();
        Assert.Contains(entries, r => OperatingSystem.IsWindows() && r.IdentityReference.Value == "S-1-5-2" && r.AccessControlType == System.Security.AccessControl.AccessControlType.Deny);
        Assert.Contains(entries, r => OperatingSystem.IsWindows() && r.IdentityReference.Value == "S-1-5-4" && r.AccessControlType == System.Security.AccessControl.AccessControlType.Allow);
    }

    [Fact]
    public void Disconnection_clears_live_figures_and_does_not_claim_the_worker_is_idle()
    {
        var model = new Optimisarr.Sidecar.Tray.MonitorViewModel();
        model.Update(new MonitorSnapshot("PC", "Connected", "Ready", false, "http://test", new MachineLoad(0.5, 0.6), 100, [], null, "test"));
        Assert.Equal("50%", model.Cpu);
        model.Disconnect("No local connection");
        Assert.Equal("—", model.Cpu);
        Assert.Equal("—", model.Free);
        Assert.False(model.Available);
        Assert.False(model.Working);
        Assert.Equal("Worker not connected", model.Title);
    }

    [Fact]
    public void A_server_connection_failure_is_not_presented_as_ready_for_work()
    {
        var model = new Optimisarr.Sidecar.Tray.MonitorViewModel();
        model.Update(new MonitorSnapshot("PC", "Unreachable", "Cannot reach the server", false, "http://test", null, null, [], null, "test"));
        Assert.Equal("NO SERVER", model.State);
        Assert.Equal("Worker needs attention", model.Title);
        Assert.Contains("Cannot reach", model.Stage);
    }

    [Theory]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://user:secret@server.test")]
    [InlineData("")]
    public void Monitor_never_opens_non_web_or_credential_bearing_addresses(string address) =>
        Assert.Null(MonitorProtocol.ServerUri(address));

    [Fact]
    public void Bare_server_address_is_a_web_url() =>
        Assert.Equal("http://server:8787/", MonitorProtocol.ServerUri("server:8787")!.AbsoluteUri);

    [Fact]
    public void Finished_jobs_leave_the_active_list_and_keep_the_outcome()
    {
        var monitor = new WorkerMonitor();
        monitor.Observe(new MonitorJob(7, "Film", "hevc_nvenc", RemoteStage.Encoding, 12));
        var previous = monitor.Read();
        monitor.Complete(7, "Returned to server");
        Assert.Single(previous.Jobs);
        Assert.Empty(monitor.Read().Jobs);
        Assert.Contains("Returned", monitor.Read().Last);
    }
}
