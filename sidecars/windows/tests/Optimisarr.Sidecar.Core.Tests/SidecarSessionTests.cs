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
        public List<int> HeartbeatCapacities { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (request.RequestUri?.AbsolutePath.EndsWith("/heartbeat", StringComparison.Ordinal) == true)
            {
                using var document = System.Text.Json.JsonDocument.Parse(
                    await request.Content!.ReadAsStringAsync(cancellationToken));
                HeartbeatCapacities.Add(document.RootElement.GetProperty("maxConcurrency").GetInt32());
            }
            var (status, json) = replies[Math.Min(_index++, replies.Length - 1)];
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
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
    public async Task Paused_worker_keeps_heartbeats_but_never_claims_work()
    {
        var handler = new QueuedHandler((HttpStatusCode.OK, Beat));
        var session = Session(handler, new InMemoryCredentialStore(
            new StoredPairing("https://example.com", "secret", 7)), runJob: TakesAnyJob());
        session.SetPaused(true);
        await session.RunAsync(CancellationToken.None);
        Assert.True(session.IsPaused);
        Assert.Equal(1, handler.Calls);
        session.SetPaused(false);
        Assert.False(session.IsPaused);
    }

    [Fact]
    public async Task Shutdown_arm_reports_zero_capacity_and_cancel_restores_previous_pause()
    {
        var handler = new QueuedHandler((HttpStatusCode.OK, Beat));
        var session = Session(handler, new InMemoryCredentialStore(
            new StoredPairing("https://example.com", "secret", 7)), runJob: TakesAnyJob());
        session.SetPaused(true);
        session.ArmShutdown();
        await session.RunAsync(CancellationToken.None);
        Assert.True(session.ShutdownReady);
        Assert.Equal([0], handler.HeartbeatCapacities);
        Assert.Equal(1, handler.Calls);
        session.CancelShutdown();
        Assert.True(session.IsPaused);
        await session.RunAsync(CancellationToken.None);
        Assert.Equal([0, 1], handler.HeartbeatCapacities);
    }

    [Fact]
    public async Task Final_shutdown_check_refuses_power_off_when_server_stops_answering()
    {
        var handler = new QueuedHandler((HttpStatusCode.OK, Beat),
            (HttpStatusCode.ServiceUnavailable, "{}"));
        var session = Session(handler, new InMemoryCredentialStore(
            new StoredPairing("https://example.com", "secret", 7)));
        session.ArmShutdown();
        await session.RunAsync(CancellationToken.None);
        Assert.True(session.ShutdownReady);
        Assert.False(await session.ConfirmShutdownAsync(CancellationToken.None));
        Assert.False(session.ShutdownReady);
        Assert.Equal(SidecarState.Unreachable, session.Status.State);
        Assert.Equal([0, 0], handler.HeartbeatCapacities);
    }

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

/// <summary>
/// Jobs run beside the check-in loop, not inside it.
///
/// <para>Awaiting one in the loop stopped the check-ins for as long as it took, so any job over
/// the server's two-minute threshold made a machine that was busily encoding look offline — and an
/// offline worker is not one the queue holds work for. It also meant a second job could never be
/// started however much the machine had spare.</para>
/// </summary>
public sealed class ConcurrentSidecarSessionTests
{
    private const string Beat =
        """{"workerId":7,"protocolVersion":1,"serverTimeUtc":"2026-09-15T20:00:00Z","heartbeatIntervalSeconds":30,"draining":false}""";

    private sealed class Handler(params (HttpStatusCode Status, string Json)[] replies) : HttpMessageHandler
    {
        private int _index;
        private bool _handedOverAJob;
        public int Beats { get; private set; }

        /// <summary>
        /// Check-ins made after a job was handed over — which, while that job is still running, is
        /// exactly the number the old shape could not have made. Counted here rather than timed by
        /// the test: the fake clock runs the loop to its end in microseconds, so anything the test
        /// samples afterwards is a race it will sometimes lose.
        /// </summary>
        public int BeatsWhileWorking { get; private set; }
        public Action? BeforeClaimReply { get; set; }
        public int Releases { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/claim", StringComparison.Ordinal)) BeforeClaimReply?.Invoke();
            if (request.RequestUri.AbsolutePath.EndsWith("/release", StringComparison.Ordinal)) Releases++;
            if (request.RequestUri.AbsolutePath.EndsWith("/heartbeat", StringComparison.Ordinal))
            {
                Beats++;
                if (_handedOverAJob)
                {
                    BeatsWhileWorking++;
                }
            }

            if (request.RequestUri.AbsolutePath.EndsWith("/claim", StringComparison.Ordinal)
                && _index < replies.Length
                && replies[_index].Json.Contains("\"jobId\"", StringComparison.Ordinal))
            {
                _handedOverAJob = true;
            }

            var (status, json) = replies[Math.Min(_index++, replies.Length - 1)];
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    /// An assignment as the server sends one. Written without interpolation: the JSON's own braces
    /// and a raw interpolated string cannot both have the last word.
    private const string AssignmentTemplate = """
        {"leaseId":"LEASE","jobId":JOB,"title":"T","sourceBytes":1,
         "videoEncoder":"hevc_nvenc","vmaf":"Cpu","expiresUtc":"2026-09-15T21:00:00Z",
         "renewWithinSeconds":30,"arguments":["-i","INPUT","OUTPUT.mkv"],
         "outputExtension":"mkv",
         "quality":{"measure":false,"model":"m","frameSubsample":1,"clipVmaf":false,
                    "minimumHarmonicMean":0,"minimumMinimum":0,"commands":[]}}
        """;

    private static string Assignment(int jobId) => AssignmentTemplate
        .Replace("LEASE", Guid.NewGuid().ToString(), StringComparison.Ordinal)
        .Replace("JOB", jobId.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);

    private static SidecarSession Session(
        Handler handler,
        int maxConcurrency,
        Func<StoredPairing, Assignment, CancellationToken, Task<JobOutcome>> runJob,
        int stopAfterBeats)
    {
        var beats = 0;
        return new SidecarSession(
            new SidecarClient(new HttpClient(handler)),
            new InMemoryCredentialStore(new StoredPairing("https://server.example.com", "good", 7)),
            probe: _ => Task.FromResult(new SidecarCapabilities(
                "PICARD", "windows", "x64", ["hevc_nvenc"], ["aac"], [],
                VmafCapability.Cuda, 1024, maxConcurrency)),
            load: () => null,
            delay: (_, _) => ++beats >= stopAfterBeats
                ? throw new OperationCanceledException()
                : Task.CompletedTask,
            runJob: runJob);
    }

    [Fact]
    public async Task Pausing_during_a_claim_returns_the_assignment_without_starting_it()
    {
        var handler = new Handler((HttpStatusCode.OK, Beat), (HttpStatusCode.OK, Assignment(1)), (HttpStatusCode.NoContent, ""));
        var ran = false;
        var session = Session(handler, 1, (_, assignment, _) =>
        {
            ran = true;
            return Task.FromResult(new JobOutcome(assignment.JobId, true, "done"));
        }, 1);
        handler.BeforeClaimReply = () => session.SetPaused(true);
        await session.RunAsync(CancellationToken.None);
        Assert.False(ran);
        Assert.Equal(1, handler.Releases);
    }

    [Fact]
    public async Task Shutdown_stays_blocked_when_an_in_flight_claim_cannot_be_handed_back()
    {
        var handler = new Handler((HttpStatusCode.OK, Beat), (HttpStatusCode.OK, Assignment(1)),
            (HttpStatusCode.ServiceUnavailable, "{}"), (HttpStatusCode.OK, Beat));
        var ran = false;
        var session = Session(handler, 1, (_, assignment, _) =>
        {
            ran = true;
            return Task.FromResult(new JobOutcome(assignment.JobId, true, "done"));
        }, 2);
        handler.BeforeClaimReply = session.ArmShutdown;

        await session.RunAsync(CancellationToken.None);

        Assert.False(ran);
        Assert.Equal(1, handler.Releases);
        Assert.True(session.HasUnacknowledgedResult);
        Assert.False(session.ShutdownReady);
    }

    [Fact]
    public async Task The_check_in_carries_on_while_a_job_runs()
    {
        // The whole point. A job that outlasts the offline threshold must not make this machine
        // look gone, because the queue stops holding work for a worker it thinks has left.
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var handler = new Handler(
            (HttpStatusCode.OK, Beat),
            (HttpStatusCode.OK, Assignment(1)),
            (HttpStatusCode.OK, Beat),
            (HttpStatusCode.NoContent, ""));

        var session = Session(handler, maxConcurrency: 1, runJob: async (_, assignment, _) =>
        {
            started.TrySetResult();
            await release.Task;
            return new JobOutcome(assignment.JobId, Delivered: true, "done");
        }, stopAfterBeats: 4);

        var loop = session.RunAsync(CancellationToken.None);
        // The job is now sitting in the middle of its work, exactly as an encode does for minutes.
        await started.Task;
        release.SetResult();
        await loop;

        Assert.True(
            handler.BeatsWhileWorking > 0,
            "the loop made no check-in while a job was running, which is how a busy machine came to look offline");
    }

    [Fact]
    public async Task A_machine_with_room_for_two_takes_two()
    {
        var running = 0;
        var peak = 0;
        var gate = new TaskCompletionSource();
        var handler = new Handler(
            (HttpStatusCode.OK, Beat),
            (HttpStatusCode.OK, Assignment(1)),
            (HttpStatusCode.OK, Assignment(2)),
            (HttpStatusCode.NoContent, ""));

        var session = Session(handler, maxConcurrency: 2, runJob: async (_, assignment, _) =>
        {
            var now = Interlocked.Increment(ref running);
            Interlocked.Exchange(ref peak, Math.Max(peak, now));
            await gate.Task;
            Interlocked.Decrement(ref running);
            return new JobOutcome(assignment.JobId, Delivered: true, "done");
        }, stopAfterBeats: 3);

        var loop = session.RunAsync(CancellationToken.None);
        while (Volatile.Read(ref peak) < 2 && !loop.IsCompleted)
        {
            await Task.Delay(5);
        }

        gate.SetResult();
        await loop;

        Assert.Equal(2, peak);
    }

    [Fact]
    public async Task It_never_takes_more_than_the_machine_said_it_could()
    {
        // The number the server was told at check-in. Exceeding it would have this machine holding
        // leases it cannot work on while other workers sit idle.
        var peak = 0;
        var running = 0;
        var gate = new TaskCompletionSource();
        var handler = new Handler(
            (HttpStatusCode.OK, Beat),
            (HttpStatusCode.OK, Assignment(1)),
            (HttpStatusCode.OK, Assignment(2)),
            (HttpStatusCode.OK, Assignment(3)));

        var session = Session(handler, maxConcurrency: 1, runJob: async (_, assignment, _) =>
        {
            var now = Interlocked.Increment(ref running);
            Interlocked.Exchange(ref peak, Math.Max(peak, now));
            await gate.Task;
            Interlocked.Decrement(ref running);
            return new JobOutcome(assignment.JobId, Delivered: true, "done");
        }, stopAfterBeats: 3);

        var loop = session.RunAsync(CancellationToken.None);
        await Task.Delay(60);
        gate.SetResult();
        await loop;

        Assert.Equal(1, peak);
    }

    [Fact]
    public async Task Stopping_waits_for_the_job_to_hand_itself_back()
    {
        // The runner releases its lease on the way out. Exiting without waiting would leave the
        // server holding a lease for a job nobody is doing until it lapsed.
        var handedBack = false;
        var handler = new Handler(
            (HttpStatusCode.OK, Beat),
            (HttpStatusCode.OK, Assignment(1)),
            (HttpStatusCode.NoContent, ""));

        var session = Session(handler, maxConcurrency: 1, runJob: async (_, assignment, token) =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
                await Task.Delay(20, CancellationToken.None);
                handedBack = true;
            }

            return new JobOutcome(assignment.JobId, Delivered: false, "released");
        }, stopAfterBeats: 99);

        using var stopping = new CancellationTokenSource();
        var loop = session.RunAsync(stopping.Token);
        await Task.Delay(60);
        await stopping.CancelAsync();
        await loop;

        Assert.True(handedBack, "the session returned before its job had handed itself back");
    }
}
