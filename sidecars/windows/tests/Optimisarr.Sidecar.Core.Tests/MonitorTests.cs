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
        foreach (var command in new byte[] { MonitorProtocol.Pause, MonitorProtocol.Read,
            MonitorProtocol.ReadPreview, MonitorProtocol.EndPreview, MonitorProtocol.Resume, 255 })
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var name = "Optimisarr-test-" + Guid.NewGuid().ToString("N");
            using var pipe = new System.IO.Pipes.NamedPipeServerStream(name, System.IO.Pipes.PipeDirection.InOut, 1,
                System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly);
            var serve = Task.Run(async () =>
            {
                if (!OperatingSystem.IsWindows()) return;
                await pipe.WaitForConnectionAsync(timeout.Token);
                await MonitorServer.ExchangeAsync(pipe, session, _ => new MonitorSnapshot("Test", "Connected", "Ready", session.IsPaused,
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

    [Fact]
    public void Armed_shutdown_explains_upload_and_exposes_cancel_without_promising_new_work()
    {
        var model = new Optimisarr.Sidecar.Tray.MonitorViewModel();
        model.Update(new MonitorSnapshot("PC", "Working", "Returning", false, "http://test", null, null,
            [new MonitorJob(7, "Film", "hevc_nvenc", RemoteStage.Delivering, null)], null, "test",
            ShutdownArmed: true, ShutdownDetail: "Waiting for 1 job", ShutdownSeconds: null));
        Assert.Equal("SHUTDOWN ARMED", model.State);
        Assert.Contains("upload", model.ShutdownDetail);
        Assert.Equal("Cancel shutdown", model.ShutdownLabel);
        Assert.False(model.CanPause);
        Assert.True(model.CanArmShutdown);
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

    [Fact]
    public void Preview_frames_are_bounded_job_scoped_and_only_exposed_to_an_active_viewer()
    {
        var now = DateTimeOffset.UtcNow;
        var monitor = new WorkerMonitor(() => now);
        monitor.Observe(new MonitorJob(7, "First", "hevc_nvenc", RemoteStage.Encoding, 12));
        monitor.Observe(new MonitorJob(8, "Second", "hevc_nvenc", RemoteStage.Encoding, 13));
        byte[] first = [0xff, 0xd8, 7, 0xff, 0xd9];
        byte[] second = [0xff, 0xd8, 8, 0xff, 0xd9];
        Assert.False(monitor.WantsPreviews);
        Assert.False(monitor.PublishPreview(7, first));
        monitor.RequestPreviews();
        Assert.True(monitor.WantsPreviews);
        Assert.True(monitor.PublishPreview(7, first));
        Assert.True(monitor.PublishPreview(8, second));
        Assert.False(monitor.PublishPreview(9, first));
        Assert.False(monitor.PublishPreview(7, new byte[MonitorProtocol.MaximumPreviewBytes + 1]));
        Assert.All(monitor.Read().Jobs, job => Assert.Null(job.PreviewJpeg));
        var jobs = monitor.Read(includePreviews: true).Jobs;
        Assert.Equal(first, jobs[0].PreviewJpeg);
        Assert.Equal(second, jobs[1].PreviewJpeg);
        monitor.Complete(7, "Delivered");
        Assert.Single(monitor.Read(includePreviews: true).Jobs);
        Assert.Equal(second, monitor.Read(includePreviews: true).Jobs[0].PreviewJpeg);
        now += TimeSpan.FromSeconds(6);
        Assert.False(monitor.WantsPreviews);
        monitor.RequestPreviews();
        Assert.Null(monitor.Read(includePreviews: true).Jobs[0].PreviewJpeg);
        monitor.EndPreviews();
        Assert.False(monitor.WantsPreviews);
    }

    [Fact]
    public void Compact_monitor_never_carries_an_old_titles_frame_into_a_new_job()
    {
        var model = new Optimisarr.Sidecar.Tray.MonitorViewModel();
        byte[] first = [0xff, 0xd8, 7, 0xff, 0xd9];
        byte[] second = [0xff, 0xd8, 8, 0xff, 0xd9];
        MonitorSnapshot WithJobs(params MonitorJob[] jobs) => new("PC", "Working", "Ready", false,
            "http://test", null, null, jobs, null, "test");
        model.Update(WithJobs(new MonitorJob(7, "First", "hevc_nvenc", RemoteStage.Encoding, 1, first),
            new MonitorJob(8, "Second", "hevc_nvenc", RemoteStage.Encoding, 2, second)));
        Assert.Equal(first, model.Preview);
        Assert.Equal(second, model.Jobs[1].PreviewJpeg);
        Assert.Equal("Job #7 · Encoding · 00:00:01 encoded", model.JobRows[0].Caption);
        Assert.Equal(second, model.JobRows[1].PreviewJpeg);
        model.Update(WithJobs(new MonitorJob(8, "Second", "hevc_nvenc", RemoteStage.Encoding, 3, second)));
        Assert.Equal(second, model.Preview);
        model.Update(WithJobs(new MonitorJob(9, "Third", "hevc_nvenc", RemoteStage.Encoding, 0)));
        Assert.Null(model.Preview);
        Assert.True(model.PreviewMissing);
        Assert.Equal("Third", model.JobRows[0].Title);
        Assert.Null(model.JobRows[0].PreviewJpeg);
        model.Update(WithJobs(new MonitorJob(8, "Second", "hevc_nvenc", RemoteStage.Encoding, 4, second)));
        model.ClearPreviews();
        Assert.Null(model.Preview);
        Assert.Null(model.Jobs[0].PreviewJpeg);
        model.Disconnect("Offline");
        Assert.Empty(model.Jobs);
    }

    [Fact]
    public void Preview_payload_stays_within_the_local_pipe_limit_even_with_more_jobs_than_preview_slots()
    {
        var monitor = new WorkerMonitor();
        monitor.RequestPreviews();
        for (var id = 1; id <= 6; id++)
        {
            monitor.Observe(new MonitorJob(id, $"Synthetic job {id}", "hevc_nvenc", RemoteStage.Encoding, id));
            var frame = new byte[MonitorProtocol.MaximumPreviewBytes];
            frame[0] = frame[^2] = 0xff;
            frame[1] = 0xd8;
            frame[^1] = 0xd9;
            Assert.Equal(id <= MonitorProtocol.MaximumPreviewJobs, monitor.PublishPreview(id, frame));
        }
        var (jobs, _) = monitor.Read(includePreviews: true);
        Assert.Equal(MonitorProtocol.MaximumPreviewJobs, jobs.Count(job => job.PreviewJpeg is not null));
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new MonitorSnapshot(
            "PC", "Working", "Ready", false, "http://test", null, null, jobs, null, "test"));
        Assert.True(bytes.Length < MonitorProtocol.MaximumResponseBytes);
    }
}
