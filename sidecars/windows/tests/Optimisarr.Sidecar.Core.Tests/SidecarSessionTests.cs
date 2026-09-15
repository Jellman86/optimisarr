using System.Net;
using System.Text;
using Optimisarr.Sidecar.Core.Capabilities;
using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Core.Tests;

/// <summary>
/// The lifecycle, with no service, no registry and no real clock: pair, beat, be drained, be
/// revoked. What matters here is which refusals end the loop and which are merely waited out —
/// get that wrong and the service either gives up on a server that was rebooting, or spends the
/// night sending credentials the server has already thrown away.
/// </summary>
public sealed class SidecarSessionTests
{
    private sealed class QueuedHandler(params (HttpStatusCode Status, string Json)[] replies) : HttpMessageHandler
    {
        private int _index;
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var (status, json) = replies[Math.Min(_index++, replies.Length - 1)];
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    private const string Beat =
        """{"workerId":7,"protocolVersion":1,"serverTimeUtc":"2026-09-14T20:00:00Z","heartbeatIntervalSeconds":30,"draining":false}""";

    private const string Draining =
        """{"workerId":7,"protocolVersion":1,"serverTimeUtc":"2026-09-14T20:00:00Z","heartbeatIntervalSeconds":30,"draining":true}""";

    private static SidecarCapabilities Capabilities() => new(
        "PICARD", "windows", "x64", ["hevc_nvenc"], ["aac"], ["hevc_cuvid"],
        VmafCapability.Cuda, 1024, 1);

    private static SidecarSession Session(
        QueuedHandler handler,
        ICredentialStore store,
        Action<SessionStatus>? report = null,
        MachineLoad? load = null,
        int stopAfterBeats = 1,
        Func<StoredPairing, Assignment, CancellationToken, Task<JobOutcome>>? runJob = null)
    {
        var beats = 0;
        return new SidecarSession(
            new SidecarClient(new HttpClient(handler)),
            store,
            probe: _ => Task.FromResult(Capabilities()),
            load: () => load,
            // Collapses the wait so a check-in loop can be walked without real time passing, and
            // ends it after a set number of beats rather than running for ever.
            delay: (_, _) => ++beats >= stopAfterBeats
                ? throw new OperationCanceledException()
                : Task.CompletedTask,
            report: report,
            runJob: runJob);
    }

    /// Enough of a job runner that the loop will actually ask for work. Without one the claim is
    /// never made, which is easy to miss: the queued reply meant for the claim is then swallowed by
    /// the next heartbeat and the test passes for the wrong reason.
    private static Func<StoredPairing, Assignment, CancellationToken, Task<JobOutcome>> TakesAnyJob() =>
        (_, assignment, _) => Task.FromResult(new JobOutcome(assignment.JobId, Delivered: true, "done"));

    [Fact]
    public async Task Pairing_stores_the_credential_before_anything_else_can_go_wrong()
    {
        var store = new InMemoryCredentialStore();
        var session = Session(
            new QueuedHandler((HttpStatusCode.OK, """{"workerId":7,"credential":"secret","protocolVersion":1}""")),
            store);

        await session.PairAsync("https://optimisarr.example.com", "123456");

        // Issued exactly once and not reissuable: losing it here would mean pairing again.
        var stored = store.Load();
        Assert.NotNull(stored);
        Assert.Equal("secret", stored.Credential);
        Assert.Equal(7, stored.WorkerId);
        Assert.Equal(SidecarState.Connected, session.Status.State);
    }

    [Fact]
    public async Task With_nothing_stored_it_says_so_and_stops_rather_than_beating_at_nobody()
    {
        var session = Session(new QueuedHandler((HttpStatusCode.OK, Beat)), new InMemoryCredentialStore());

        await session.RunAsync(CancellationToken.None);

        Assert.Equal(SidecarState.Unpaired, session.Status.State);
        Assert.Contains("--pair", session.Status.Detail);
    }

    [Fact]
    public async Task A_revoked_credential_stops_the_loop_and_is_thrown_away()
    {
        var store = new InMemoryCredentialStore(
            new StoredPairing("https://optimisarr.example.com", "dead", 7));
        var handler = new QueuedHandler((HttpStatusCode.Unauthorized, """{"error":"Unknown or revoked worker credential."}"""));
        var session = Session(handler, store, stopAfterBeats: 5);

        await session.RunAsync(CancellationToken.None);

        Assert.Equal(SidecarState.Stopped, session.Status.State);
        // Exactly one attempt: retrying a credential the server has discarded is an endless run of
        // refusals, and only a person issuing a new PIN can fix it.
        Assert.Equal(1, handler.Calls);
        Assert.Null(store.Load());
    }

    [Fact]
    public async Task An_unreachable_server_is_waited_out_with_the_credential_kept()
    {
        var store = new InMemoryCredentialStore(
            new StoredPairing("https://optimisarr.example.com", "good", 7));
        var handler = new QueuedHandler(
            (HttpStatusCode.ServiceUnavailable, "{}"),
            (HttpStatusCode.ServiceUnavailable, "{}"),
            (HttpStatusCode.OK, Beat));
        var session = Session(handler, store, stopAfterBeats: 3);

        await session.RunAsync(CancellationToken.None);

        // A server that was restarting must not cost a pairing.
        Assert.NotNull(store.Load());
        Assert.Equal(SidecarState.Connected, session.Status.State);
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task Being_drained_is_reported_without_ending_the_session()
    {
        var store = new InMemoryCredentialStore(
            new StoredPairing("https://optimisarr.example.com", "good", 7));
        var reported = new List<SessionStatus>();
        var session = Session(new QueuedHandler((HttpStatusCode.OK, Draining)), store, reported.Add);

        await session.RunAsync(CancellationToken.None);

        // Draining is the server asking for no new work, not a fault: the worker keeps checking in
        // so it can be resumed without anybody touching this machine.
        Assert.Equal(SidecarState.Connected, session.Status.State);
        Assert.Contains(reported, status => status.Detail.Contains("taking no more"));
        Assert.NotNull(store.Load());
    }

    [Fact]
    public async Task An_assignment_it_cannot_read_faults_the_round_and_not_the_service()
    {
        // What PICARD did for a day. The server began sending a measurement command as a list of
        // lists; that build expected a list of strings; the claim threw a JsonException on the way
        // in. Nothing caught it, the host is configured to stop when a background service throws,
        // and the service was gone within a second of every boot — so the fleet showed a worker
        // that was simply never online, with no failure anywhere to look at.
        var store = new InMemoryCredentialStore(
            new StoredPairing("https://optimisarr.example.com", "good", 7));
        var reported = new List<SessionStatus>();
        var handler = new QueuedHandler(
            (HttpStatusCode.OK, Beat),
            // The claim, in a shape this build cannot read.
            (HttpStatusCode.OK, """{"leaseId":"not-a-guid-shaped-thing","jobId":"twelve"}"""),
            (HttpStatusCode.OK, Beat),
            // And then nothing to do, which is what the server says most of the time.
            (HttpStatusCode.NoContent, ""));
        var session = Session(handler, store, reported.Add, stopAfterBeats: 3, runJob: TakesAnyJob());

        await session.RunAsync(CancellationToken.None);

        // It carried on: three rounds asked for, three rounds made.
        Assert.True(handler.Calls >= 3, $"expected the loop to keep checking in, it made {handler.Calls} calls");
        // And it said something a person can act on rather than disappearing.
        Assert.Contains(reported, status => status.State == SidecarState.Faulted);
        Assert.NotNull(store.Load());
    }

    [Fact]
    public async Task A_fault_is_not_reported_as_the_server_being_unreachable()
    {
        // The two need telling apart: unreachable says nothing is wrong here and the server will
        // come back, while a fault says this machine could not do something and an operator may
        // have to look at it. Collapsing them is how a broken worker hides among restarting ones.
        var store = new InMemoryCredentialStore(
            new StoredPairing("https://optimisarr.example.com", "good", 7));
        var reported = new List<SessionStatus>();
        var handler = new QueuedHandler(
            (HttpStatusCode.OK, Beat),
            (HttpStatusCode.OK, """{"leaseId":"x","jobId":"twelve"}"""));
        // Ends at the first wait, so what is recorded is exactly the round that faulted.
        var session = Session(handler, store, reported.Add, stopAfterBeats: 1, runJob: TakesAnyJob());

        await session.RunAsync(CancellationToken.None);

        Assert.Equal(
            [SidecarState.Connected, SidecarState.Faulted],
            reported.Select(status => status.State));
    }
}
