using System.Net;
using System.Text;
using System.Text.Json;
using Optimisarr.Core.Workers;
using Optimisarr.Sidecar.Core.Capabilities;
using Optimisarr.Sidecar.Core.Session;
using VmafCapability = Optimisarr.Sidecar.Core.Capabilities.VmafCapability;

namespace Optimisarr.Sidecar.Core.Tests;

public sealed class PerceptualCapacityReportingTests
{
    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public void Advance() => Interlocked.Add(ref _ticks, TimeSpan.FromSeconds(5).Ticks);
    }

    private sealed class Server : HttpMessageHandler
    {
        public List<JsonElement> Reports = [];
        public int Claims;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/claim"))
            {
                Claims++;
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(PerceptualJobFixture.Assignment(), SidecarClient.Json), Encoding.UTF8, "application/json") };
            }
            Reports.Add(JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)).RootElement.Clone());
            return new(HttpStatusCode.OK) { Content = new StringContent("""{"workerId":1,"protocolVersion":10,"heartbeatIntervalSeconds":5,"draining":false}""", Encoding.UTF8, "application/json") };
        }
    }
    private static SidecarCapabilities Capabilities() => new("fixture", "linux", "x64", [], ["aac"], [],
        VmafCapability.None, 10000, 1, Perceptual: new(AvailableMemoryBytes: 1000, ScratchUsesMemory: false));

    [Fact]
    public void Live_advertisement_deducts_all_native_reservations_until_their_cleanup_releases_them()
    {
        var pool = new PerceptualJobResourcePool();
        var root = Path.GetTempPath();
        using var first = pool.TryAcquire(root, 600, 400, 1000, 1000);
        Assert.Equal((400L, 600L), pool.Available(root, 1000, 1000));
        Assert.Equal((0L, 0L), pool.Available(root, long.MinValue, long.MinValue));
        first!.Dispose();
        Assert.Equal((1000L, 1000L), pool.Available(root, 1000, 1000));
        Assert.Equal((0L, 100L), pool.Available(root, -1, 100));
    }

    [Fact]
    public async Task Heartbeats_refresh_native_resources_and_transmit_the_proof_without_reprobing_encoders()
    {
        using var server = new Server(); using var http = new HttpClient(server);
        var reads = 0; var probes = 0;
        var session = new SidecarSession(new(http), new InMemoryCredentialStore(new("https://fixture.local", "test", 1)),
            _ => { probes++; return Task.FromResult(Capabilities()); }, () => null,
            (_, _) => reads >= 2 ? throw new OperationCanceledException() : Task.CompletedTask,
            refreshCapabilities: cap => cap with { Perceptual = cap.Perceptual! with { AvailableMemoryBytes = ++reads * 100 } });
        await session.RunAsync(default);
        Assert.Equal(1, probes);
        Assert.Equal([100L, 200L], server.Reports.Select(report => report.GetProperty("perceptual").GetProperty("availableMemoryBytes").GetInt64()));
    }

    [Fact]
    public async Task Resource_refusal_keeps_heartbeats_running_but_holds_claims_until_the_cooldown_expires()
    {
        using var server = new Server(); using var http = new HttpClient(server);
        var clock = new Clock();
        var finished = new SemaphoreSlim(0); var waits = 0;
        var session = new SidecarSession(new(http), new InMemoryCredentialStore(new("https://fixture.local", "test", 1)),
            _ => Task.FromResult(Capabilities()), () => null,
            async (_, token) =>
            {
                if (waits is 0 or 6) await finished.WaitAsync(token);
                clock.Advance();
                if (++waits >= 8) throw new OperationCanceledException();
            }, report: status => { if (status.Detail.Contains("capacity hold")) finished.Release(); },
            runJob: (_, a, _) => Task.FromResult(new JobOutcome(a.JobId, false, "capacity hold", CapacityBlocked: true)),
            clock: clock);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await session.RunAsync(deadline.Token);
        Assert.Equal(2, server.Claims);
        Assert.Equal(8, server.Reports.Count);
    }
}
