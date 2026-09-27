using System.Net;
using System.Text.Json;
using Optimisarr.Sidecar.Core.Session;
using Optimisarr.Sidecar.Linux;

namespace Optimisarr.Sidecar.Linux.Tests;

public sealed class DashboardTests
{
    [Fact]
    public void Snapshot_removes_finished_jobs_and_never_exposes_server_query_secrets()
    {
        var view = new WorkerDashboard("Quark", "https://server.test/base?token=secret", "/work", 1);
        view.Observe(new MonitorJob(42, "Example", "hevc_qsv", RemoteStage.Encoding, 12));
        var active = view.Snapshot(new ScratchStorage("RAM", 100, 200));
        Assert.Single(active.Jobs);
        Assert.Equal("https://server.test/base", active.ServerAddress);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(active));
        view.Finish(42, true);
        Assert.Empty(view.Snapshot(new ScratchStorage("Disk", 100, 200)).Jobs);
        Assert.Equal("Job 42 returned its result to the server", view.Snapshot(new ScratchStorage("Disk", 100, 200)).LastOutcome);
    }

    [Fact]
    public void A_drained_worker_does_not_appear_ready_for_work()
    {
        var view = new WorkerDashboard("Quark", null, "/work", 1);
        view.Status(new SessionStatus(SidecarState.Connected, "connected"), draining: true);
        Assert.Equal("Draining", view.Snapshot(new ScratchStorage("RAM", 1, 2)).State);
    }

    [Fact]
    public void Preview_is_bounded_and_a_late_callback_cannot_restore_a_finished_job()
    {
        var view = new WorkerDashboard("Quark", null, "/work", 1);
        var job = new MonitorJob(42, "Clip", "hevc_qsv", RemoteStage.Encoding, 12);
        view.Observe(job);
        view.Preview(42, new byte[MonitorProtocol.MaximumPreviewBytes + 1]);
        Assert.Null(view.ReadPreview(42));
        view.Preview(42, [0xff, 0xd8, 0xff, 0xd9]);
        Assert.NotNull(view.ReadPreview(42));
        view.Finish(42, true);
        view.Observe(job);
        view.Preview(42, [0xff, 0xd8, 0xff, 0xd9]);
        Assert.Null(view.ReadPreview(42));
        Assert.Empty(view.Snapshot(new ScratchStorage("RAM", 1, 2)).Jobs);
    }

    [Fact]
    public async Task Dashboard_is_read_only_and_status_cannot_be_cached()
    {
        var view = new WorkerDashboard("Quark", null, Path.GetTempPath(), 1);
        await using var app = DashboardHost.Create(view, "http://127.0.0.1:0");
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var status = await client.GetAsync("/api/sidecar/status");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.True(status.Headers.CacheControl?.NoStore);
        Assert.Contains("Quark", await status.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.PostAsync("/api/sidecar/status", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/health")).StatusCode);
        await app.StopAsync();
    }
}
