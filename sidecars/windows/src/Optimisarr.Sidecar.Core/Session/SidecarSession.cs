namespace Optimisarr.Sidecar.Core.Session;

/// <summary>Where the session is, in terms an operator would recognise.</summary>
public enum SidecarState
{
    /// <summary>No credential stored. Somebody needs to pair this machine.</summary>
    Unpaired,

    /// <summary>Paired and checking in successfully.</summary>
    Connected,

    /// <summary>Running a job the server handed over.</summary>
    Working,

    /// <summary>Paired, but the last check-in did not get through. Recovers on its own.</summary>
    Unreachable,

    /// <summary>
    /// The last round failed for a reason this machine did not expect — an assignment it could not
    /// read, a file it could not write. It keeps checking in.
    ///
    /// <para>Distinct from <see cref="Unreachable"/> on purpose: that one means the server could not
    /// be reached and says nothing is wrong here. This one means something here is wrong, and an
    /// operator looking at the machine should be able to tell those apart at a glance.</para>
    /// </summary>
    Faulted,

    /// <summary>
    /// The server refused the credential, or the feature is off in a way only a person can undo.
    /// Retrying cannot help, so the loop stops rather than spending the night on it.
    /// </summary>
    Stopped,
}

/// <summary>What the session is doing, and why.</summary>
public sealed record SessionStatus(SidecarState State, string Detail);

/// <summary>
/// Pairs, then checks in for as long as it is allowed to.
///
/// Split from any hosting so the whole lifecycle — pair, beat, be told to drain, be revoked — runs
/// in a test in milliseconds, with no service, no registry and no clock.
/// </summary>
public sealed class SidecarSession(
    SidecarClient client,
    ICredentialStore store,
    Func<CancellationToken, Task<Capabilities.SidecarCapabilities>> probe,
    Func<MachineLoad?> load,
    Func<TimeSpan, CancellationToken, Task> delay,
    Action<SessionStatus>? report = null,
    Func<StoredPairing, Assignment, CancellationToken, Task<JobOutcome>>? runJob = null)
{
    private int _paused;
    public bool IsPaused => Volatile.Read(ref _paused) != 0;
    public void SetPaused(bool paused)
    {
        if (ShutdownArmed) return;
        Interlocked.Exchange(ref _paused, paused ? 1 : 0);
    }

    private int _shutdownArmed;
    private readonly SemaphoreSlim _heartbeatWake = new(0, 1);
    private int _pausedBeforeShutdown;
    private long _drainedHeartbeatTicks;
    private int _unacknowledgedResult;
    private StoredPairing? _runningPairing;
    private Capabilities.SidecarCapabilities? _runningCapabilities;
    public bool ShutdownArmed => Volatile.Read(ref _shutdownArmed) != 0;
    public int ActiveJobCount => Running;
    public bool HasUnacknowledgedResult => Volatile.Read(ref _unacknowledgedResult) != 0;
    public bool ShutdownReady => ShutdownArmed && Running == 0
        && !HasUnacknowledgedResult
        && Status.State == SidecarState.Connected
        && new DateTime(Interlocked.Read(ref _drainedHeartbeatTicks), DateTimeKind.Utc) >= DateTime.UtcNow.AddSeconds(-35);

    public void ArmShutdown()
    {
        if (Interlocked.Exchange(ref _shutdownArmed, 1) != 0) return;
        _pausedBeforeShutdown = IsPaused ? 1 : 0;
        Interlocked.Exchange(ref _drainedHeartbeatTicks, 0);
        WakeHeartbeat();
    }

    public void CancelShutdown()
    {
        if (Interlocked.Exchange(ref _shutdownArmed, 0) == 0) return;
        Interlocked.Exchange(ref _drainedHeartbeatTicks, 0);
        Interlocked.Exchange(ref _paused, _pausedBeforeShutdown);
        WakeHeartbeat();
    }

    private void WakeHeartbeat()
    {
        try { _heartbeatWake.Release(); }
        catch (SemaphoreFullException) { }
    }

    /// <summary>A fresh server acknowledgement immediately before power-off, not a cached check-in.</summary>
    public async Task<bool> ConfirmShutdownAsync(CancellationToken token)
    {
        var pairing = _runningPairing;
        var capabilities = _runningCapabilities;
        if (!ShutdownReady || pairing is null || capabilities is null) return false;
        try
        {
            await client.HeartbeatAsync(pairing, capabilities with { MaxConcurrency = 0 }, load(), token);
            Interlocked.Exchange(ref _drainedHeartbeatTicks, DateTime.UtcNow.Ticks);
            return ShutdownArmed && Running == 0 && !HasUnacknowledgedResult;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return false; }
        catch (Exception error)
        {
            Interlocked.Exchange(ref _drainedHeartbeatTicks, 0);
            Set(SidecarState.Unreachable, "Final shutdown check failed: " + error.Message);
            return false;
        }
    }

    public string? ServerAddress { get; private set; }

    public SessionStatus Status { get; private set; } = new(SidecarState.Unpaired, "Not paired");

    /// <summary>The jobs running beside the check-in loop, by id, so they can be waited for.</summary>
    private readonly Dictionary<int, Task> _jobs = [];

    /// <summary>Guards the status, which several jobs and the loop all write.</summary>
    private readonly Lock _statusGate = new();

    /// <summary>
    /// Redeems a PIN and stores the credential immediately — the server issues it exactly once and
    /// cannot reissue it, so anything that went wrong after this point would cost the pairing.
    /// </summary>
    public async Task<StoredPairing> PairAsync(
        string serverAddress, string pin, CancellationToken cancellationToken = default)
    {
        var capabilities = await probe(cancellationToken);
        var result = await client.PairAsync(serverAddress, pin, capabilities, cancellationToken);
        var pairing = new StoredPairing(serverAddress, result.Credential, result.WorkerId);
        store.Save(pairing);
        ServerAddress = pairing.ServerAddress;
        Set(SidecarState.Connected, $"Paired as worker {result.WorkerId}");
        return pairing;
    }

    /// <summary>
    /// Checks in until told to stop. Returns when the credential is refused or the token is
    /// cancelled; a server that is merely unreachable is waited out rather than given up on.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var pairing = store.Load();
        ServerAddress = pairing?.ServerAddress;
        if (pairing is null)
        {
            Set(SidecarState.Unpaired, "Not paired. Run with --pair to redeem a pairing code.");
            return;
        }

        // Re-probed on every start, not only at pairing: FFmpeg gets rebuilt, a driver stops
        // working, an encoder that used to open no longer does. Without this the server would keep
        // scheduling against whatever was true the day the two were introduced.
        var capabilities = await probe(cancellationToken);
        _runningPairing = pairing;
        _runningCapabilities = capabilities;
        var interval = TimeSpan.FromSeconds(30);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var reportingDrain = ShutdownArmed;
                var beat = await client.HeartbeatAsync(pairing,
                    reportingDrain ? capabilities with { MaxConcurrency = 0 } : capabilities,
                    load(), cancellationToken);
                if (reportingDrain && ShutdownArmed)
                    Interlocked.Exchange(ref _drainedHeartbeatTicks, DateTime.UtcNow.Ticks);
                interval = beat.Interval;
                Set(
                    SidecarState.Connected,
                    beat.Draining
                        ? $"Worker {beat.WorkerId}: finishing current work, taking no more"
                        : $"Worker {beat.WorkerId}: connected");

                // Asking is free and almost always answered with "nothing". Draining is the server
                // saying it wants this machine to stop taking work, so it is not even asked.
                //
                // Filled up to capacity rather than one at a time, and — the part that matters —
                // the jobs run *beside* this loop rather than inside it. Awaiting one here stopped
                // the check-ins for as long as it took, so any job over two minutes made a machine
                // that was busily encoding look offline to the server, and an offline worker is not
                // one the queue holds work for. It also meant a second job could never be started
                // however much the machine had spare.
                while (runJob is not null
                    && !beat.Draining
                    && !IsPaused
                    && !ShutdownArmed
                    && Running < capabilities.MaxConcurrency
                    && !cancellationToken.IsCancellationRequested)
                {
                    var assignment = await client.ClaimAsync(pairing, cancellationToken);
                    if (assignment is null)
                    {
                        break;
                    }

                    // A pause can arrive while the claim request is in flight.
                    if (IsPaused || ShutdownArmed)
                    {
                        try
                        {
                            await client.ReleaseAsync(pairing, assignment.LeaseId, cancellationToken);
                        }
                        catch (Exception error)
                        {
                            // A claim can return after shutdown was armed. Until its hand-back
                            // is acknowledged, the server may still believe we hold this lease.
                            Interlocked.Exchange(ref _unacknowledgedResult, 1);
                            Set(SidecarState.Faulted,
                                $"Job {assignment.JobId}: hand-back was not acknowledged: {error.Message}");
                        }
                        break;
                    }
                    Start(runJob, pairing, assignment, cancellationToken);
                }
            }
            catch (SidecarException exception) when (!exception.Recoverable)
            {
                Interlocked.Exchange(ref _drainedHeartbeatTicks, 0);
                // A revoked credential or a protocol the server will not speak. Beating on would be
                // an endless run of refusals, and the stored secret is worthless either way.
                store.Clear();
                await DrainAsync();
                Set(SidecarState.Stopped, exception.Message);
                return;
            }
            catch (Exception exception) when (exception is SidecarException or HttpRequestException)
            {
                Interlocked.Exchange(ref _drainedHeartbeatTicks, 0);
                // The server is down, or restarting, or the feature is off for a moment. The
                // credential is still believed good, so this recovers by itself.
                Set(SidecarState.Unreachable, exception.Message);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await DrainAsync();
                return;
            }
            catch (Exception exception)
            {
                Interlocked.Exchange(ref _drainedHeartbeatTicks, 0);
                // Anything at all that was not foreseen. It keeps checking in rather than ending
                // the loop, because ending it stops the whole service: the host is configured to
                // stop when a background service throws, and nothing restarts it until a person
                // does. PICARD spent a day like that — the server began sending a measurement
                // command as a list of lists, that build expected a list of strings, and the claim
                // threw a JsonException on the way in. The service stopped inside a second of every
                // boot, and the fleet simply showed a worker that was never online.
                //
                // One assignment this machine cannot read says nothing about the next one, and a
                // machine that is up and complaining is worth far more than one that is silently
                // gone. The delay below still applies, so a persistent fault costs one check-in
                // interval a time rather than a hot loop.
                Set(SidecarState.Faulted, exception.Message);
            }

            try
            {
                using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var timer = delay(interval, waiting.Token);
                var wake = _heartbeatWake.WaitAsync(waiting.Token);
                await await Task.WhenAny(timer, wake);
                waiting.Cancel();
            }
            catch (OperationCanceledException)
            {
                await DrainAsync();
                return;
            }
        }

        await DrainAsync();
    }

    /// <summary>How many jobs are running beside the check-in loop right now.</summary>
    private int Running
    {
        get { lock (_jobs) { return _jobs.Count; } }
    }

    /// <summary>
    /// Starts a job alongside the loop and forgets about it until it finishes.
    ///
    /// <para>Deliberately not awaited. The loop's whole purpose while a job runs is to keep saying
    /// this machine is alive, and it cannot do that from inside the job.</para>
    ///
    /// <para>Nothing thrown by a job reaches the loop, so a job that fails in a way nobody foresaw
    /// costs that job and not the service. The status it leaves behind is the last word on it.</para>
    /// </summary>
    private void Start(
        Func<StoredPairing, Assignment, CancellationToken, Task<JobOutcome>> run,
        StoredPairing pairing,
        Assignment assignment,
        CancellationToken cancellationToken)
    {
        lock (_jobs)
        {
            // The server should never offer the same job twice, but a worker that ran it twice
            // would encode the same title into two files and deliver whichever finished last.
            if (_jobs.ContainsKey(assignment.JobId))
            {
                return;
            }

            _jobs[assignment.JobId] = Task.CompletedTask;
        }

        Set(SidecarState.Working, $"Job {assignment.JobId}: {assignment.Title}");

        var running = Task.Run(
            async () =>
            {
                try
                {
                    var outcome = await run(pairing, assignment, cancellationToken);
                    Finish($"Job {outcome.JobId}: {outcome.Detail}", assignment.JobId);
                }
                catch (OperationCanceledException)
                {
                    // The service is stopping. The runner hands the job back on its way out, so
                    // the server has it again within a check-in rather than a lease's lifetime.
                    Finish($"Job {assignment.JobId}: stopped", assignment.JobId);
                }
                catch (Exception exception)
                {
                    Interlocked.Exchange(ref _unacknowledgedResult, 1);
                    Finish($"Job {assignment.JobId}: {exception.Message}", assignment.JobId, faulted: true);
                }
            },
            CancellationToken.None);

        lock (_jobs)
        {
            // Only if it is still listed: a job short enough to finish before this line has already
            // removed itself, and putting its task back would leave one nothing ever waits on.
            if (_jobs.ContainsKey(assignment.JobId))
            {
                _jobs[assignment.JobId] = running;
            }
        }
    }

    /// <summary>
    /// Waits for whatever is still running, so a stopping service hands its jobs back rather than
    /// vanishing with them.
    ///
    /// <para>The token is already cancelled by the time this is reached, so each runner is on its
    /// way out and releasing its lease. Without this the process would exit mid-encode and the
    /// server would wait a full lease for a job it could have had in seconds.</para>
    /// </summary>
    private async Task DrainAsync()
    {
        Task[] outstanding;
        lock (_jobs)
        {
            outstanding = [.. _jobs.Values];
        }

        if (outstanding.Length == 0)
        {
            return;
        }

        Set(SidecarState.Working, $"Stopping: handing back {outstanding.Length} job(s)");
        // Bounded: a runner wedged on a network call must not hold the service open for ever.
        // Windows stops waiting long before this and kills the process anyway.
        await Task.WhenAny(Task.WhenAll(outstanding), Task.Delay(TimeSpan.FromSeconds(20)));
    }

    /// <summary>Records how a job ended and says what this machine is doing now.</summary>
    private void Finish(string detail, int jobId, bool faulted = false)
    {
        lock (_jobs)
        {
            _jobs.Remove(jobId);
        }

        // Working while others are still going, connected when the machine is idle again. Said
        // from here rather than from the loop because the loop may be asleep between check-ins.
        Set(faulted ? SidecarState.Faulted : (Running > 0 ? SidecarState.Working : SidecarState.Connected), detail);
    }

    private void Set(SidecarState state, string detail)
    {
        SessionStatus status;
        lock (_statusGate)
        {
            status = new SessionStatus(state, detail);
            Status = status;
        }

        report?.Invoke(status);
    }
}
