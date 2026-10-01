using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using System.Text.Json;
using Optimisarr.Sidecar.Core.Session;
using Optimisarr.Sidecar.Linux;

namespace Optimisarr.Sidecar.Linux.Tests;

public sealed class DashboardTests
{
    [Fact]
    public void Audio_monitor_kind_survives_projection_and_clears_after_delivery()
    {
        var view = new WorkerDashboard("Audio worker", null, "/work", 1);
        view.Observe(new MonitorJob(42, "Audio fixture", "libopus", RemoteStage.Encoding, 12,
            Kind: Optimisarr.Core.Domain.MediaKind.Audio));
        Assert.Equal("Audio", Assert.Single(view.Snapshot(new ScratchStorage("Disk", 1, 2)).Jobs).Kind);
        view.Finish(42, true);
        Assert.Empty(view.Snapshot(new ScratchStorage("Disk", 1, 2)).Jobs);
    }

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
        var after = view.Snapshot(new ScratchStorage("Disk", 100, 200));
        Assert.Empty(after.Jobs);
        var finished = Assert.Single(after.Recent);
        Assert.Equal((42, "Example", true), (finished.JobId, finished.Title, finished.Delivered));
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

    private static DashboardSnapshot Status(WorkerDashboard view) => view.Snapshot(new ScratchStorage("RAM", 1, 2));

    private static Assignment Assigned(int jobId, string title = "Clip") => new(Guid.NewGuid(), jobId, title, 1024, "hevc_qsv",
        "Cpu", DateTimeOffset.UtcNow.AddMinutes(5), 60, ["-i", "{{input}}", "{{output}}.mkv"], "mkv",
        new QualityRequirement(false, "", 1, false, 0, 0, []));

    [Fact]
    public void Recent_work_is_short_and_newest_first()
    {
        var view = new WorkerDashboard("Quark", null, "/work", 1);
        for (var id = 1; id <= 8; id++)
        {
            view.Start(Assigned(id, $"Episode {id}"));
            view.Finish(id, id % 2 == 0);
        }
        var recent = Status(view).Recent;
        Assert.Equal([8, 7, 6, 5, 4], recent.Select(job => job.JobId));
        Assert.Equal("Episode 8", recent[0].Title);
    }

    [Fact]
    public void The_servers_brand_and_update_notice_reach_the_page()
    {
        var view = new WorkerDashboard("Quark", null, "/work", 1);
        Assert.Equal("precession", Status(view).BrandStyle);
        view.Server("stellar", new SidecarUpdate("0.2.17", new Uri("https://github.com/Jellman86/optimisarr/releases/tag/v0.2.17")));
        var status = Status(view);
        Assert.Equal("stellar", status.BrandStyle);
        Assert.Equal("0.2.17", status.Update!.Version);
    }

    [Fact]
    public void Artwork_is_kept_only_for_active_jobs_and_only_as_an_image()
    {
        var view = new WorkerDashboard("Quark", null, "/work", 1);
        view.Artwork(7, new LeaseArtwork([1, 2, 3], "image/jpeg"));
        Assert.Null(view.ReadArtwork(7));
        view.Start(Assigned(7));
        view.Artwork(7, new LeaseArtwork([1, 2, 3], "text/html"));
        Assert.Null(view.ReadArtwork(7));
        view.Artwork(7, new LeaseArtwork([1, 2, 3], "image/webp"));
        view.Observe(new MonitorJob(7, "Clip", "hevc_qsv", RemoteStage.FetchingSource, null));
        Assert.True(Assert.Single(Status(view).Jobs).HasArtwork);
        Assert.Equal("image/webp", view.ReadArtwork(7)!.ContentType);
        view.Finish(7, true);
        Assert.Null(view.ReadArtwork(7));
    }

    private static async Task<(WebApplication App, HttpClient Client)> Serve(WorkerDashboard view)
    {
        var app = DashboardHost.Create(view, "http://127.0.0.1:0");
        await app.StartAsync();
        return (app, new HttpClient { BaseAddress = new Uri(app.Urls.Single()) });
    }

    private static readonly StoredPairing Paired = new("https://optimisarr.example", "secret", 3);

    [Fact]
    public async Task An_unpaired_worker_pairs_from_its_own_page_exactly_once()
    {
        var view = new WorkerDashboard("Quark", null, Path.GetTempPath(), 1);
        var (app, client) = await Serve(view);
        await using var host = app;
        (string Server, string Code)? used = null;
        var waiting = view.Pairing.WaitAsync(null, null, (server, code, _) =>
        {
            used = (server, code);
            return Task.FromResult(Paired);
        }, CancellationToken.None);
        Assert.True(Status(view).Pairing.Required);
        Assert.Equal("Unpaired", Status(view).State);

        using var accepted = await client.PostAsJsonAsync("/api/sidecar/pair", new { server = " https://optimisarr.example ", code = "1234 5678" });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(("https://optimisarr.example", "12345678"), used);
        Assert.Equal(Paired, await waiting);
        Assert.False(Status(view).Pairing.Required);

        using var again = await client.PostAsJsonAsync("/api/sidecar/pair", new { server = "https://other.example", code = "12345678" });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task A_refused_code_is_explained_and_the_worker_keeps_waiting()
    {
        var view = new WorkerDashboard("Quark", null, Path.GetTempPath(), 1);
        var (app, client) = await Serve(view);
        await using var host = app;
        var attempts = 0;
        var waiting = view.Pairing.WaitAsync(null, null, (_, _, _) => ++attempts == 1
            ? throw new SidecarException("That pairing code was not accepted.", recoverable: false)
            : Task.FromResult(Paired), CancellationToken.None);

        using var refused = await client.PostAsJsonAsync("/api/sidecar/pair", new { server = "https://optimisarr.example", code = "00000000" });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("not accepted", await refused.Content.ReadAsStringAsync());
        Assert.Equal("That pairing code was not accepted.", Status(view).Pairing.Problem);
        Assert.False(waiting.IsCompleted);

        using var accepted = await client.PostAsJsonAsync("/api/sidecar/pair", new { server = "https://optimisarr.example", code = "11111111" });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        await waiting;
        Assert.Null(Status(view).Pairing.Problem);
    }

    [Theory]
    [InlineData("file:///etc/passwd", "12345678")]
    [InlineData("https://user:pass@optimisarr.example", "12345678")]
    [InlineData("https://optimisarr.example", "1234")]
    [InlineData("https://optimisarr.example", "abcdefgh")]
    public async Task Malformed_pairing_details_never_reach_the_server(string server, string code)
    {
        var view = new WorkerDashboard("Quark", null, Path.GetTempPath(), 1);
        var (app, client) = await Serve(view);
        await using var host = app;
        var called = false;
        _ = view.Pairing.WaitAsync(null, null, (_, _, _) => { called = true; return Task.FromResult(Paired); }, CancellationToken.None);

        using var response = await client.PostAsJsonAsync("/api/sidecar/pair", new { server, code });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(called);
    }

    [Fact]
    public async Task A_configured_server_cannot_be_swapped_from_the_page()
    {
        var view = new WorkerDashboard("Quark", null, Path.GetTempPath(), 1);
        var (app, client) = await Serve(view);
        await using var host = app;
        string? used = null;
        var waiting = view.Pairing.WaitAsync("https://configured.example/", null, (server, _, _) => { used = server; return Task.FromResult(Paired); }, CancellationToken.None);
        Assert.Equal("https://configured.example/", Status(view).Pairing.ConfiguredServer);

        (await client.PostAsJsonAsync("/api/sidecar/pair", new { server = "https://attacker.example", code = "12345678" })).EnsureSuccessStatusCode();
        Assert.Equal("https://configured.example/", used);
        await waiting;
    }

    [Fact]
    public async Task Another_site_cannot_submit_a_pairing()
    {
        var view = new WorkerDashboard("Quark", null, Path.GetTempPath(), 1);
        var (app, client) = await Serve(view);
        await using var host = app;
        var called = false;
        _ = view.Pairing.WaitAsync(null, null, (_, _, _) => { called = true; return Task.FromResult(Paired); }, CancellationToken.None);

        using var form = await client.PostAsync("/api/sidecar/pair",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["server"] = "https://evil.example", ["code"] = "12345678" }));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, form.StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/sidecar/pair")
        {
            Content = JsonContent.Create(new { server = "https://evil.example", code = "12345678" }),
        };
        request.Headers.Add("Sec-Fetch-Site", "cross-site");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(request)).StatusCode);
        Assert.False(called);
    }

    [Fact]
    public async Task Pairing_is_closed_when_the_worker_is_not_waiting_for_it()
    {
        var view = new WorkerDashboard("Quark", null, Path.GetTempPath(), 1);
        var (app, client) = await Serve(view);
        await using var host = app;
        using var response = await client.PostAsJsonAsync("/api/sidecar/pair", new { server = "https://optimisarr.example", code = "12345678" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Job_artwork_is_served_with_its_own_image_type()
    {
        var view = new WorkerDashboard("Quark", null, Path.GetTempPath(), 1);
        var (app, client) = await Serve(view);
        await using var host = app;
        view.Start(Assigned(9));
        view.Artwork(9, new LeaseArtwork([0x89, 0x50], "image/png"));
        using var artwork = await client.GetAsync("/api/sidecar/jobs/9/artwork");
        Assert.Equal("image/png", artwork.Content.Headers.ContentType!.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/sidecar/jobs/10/artwork")).StatusCode);
    }
}
