namespace Optimisarr.Sidecar.Core.Session;

/// <summary>Where the session is, in terms an operator would recognise.</summary>
public enum SidecarState
{
    /// <summary>No credential stored. Somebody needs to pair this machine.</summary>
    Unpaired,

    /// <summary>Paired and checking in successfully.</summary>
    Connected,

    /// <summary>Paired, but the last check-in did not get through. Recovers on its own.</summary>
    Unreachable,

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
    Action<SessionStatus>? report = null)
{
    public SessionStatus Status { get; private set; } = new(SidecarState.Unpaired, "Not paired");

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
        if (pairing is null)
        {
            Set(SidecarState.Unpaired, "Not paired. Run with --pair to redeem a pairing code.");
            return;
        }

        // Re-probed on every start, not only at pairing: FFmpeg gets rebuilt, a driver stops
        // working, an encoder that used to open no longer does. Without this the server would keep
        // scheduling against whatever was true the day the two were introduced.
        var capabilities = await probe(cancellationToken);
        var interval = TimeSpan.FromSeconds(30);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var beat = await client.HeartbeatAsync(pairing, capabilities, load(), cancellationToken);
                interval = beat.Interval;
                Set(
                    SidecarState.Connected,
                    beat.Draining
                        ? $"Worker {beat.WorkerId}: finishing current work, taking no more"
                        : $"Worker {beat.WorkerId}: connected");
            }
            catch (SidecarException exception) when (!exception.Recoverable)
            {
                // A revoked credential or a protocol the server will not speak. Beating on would be
                // an endless run of refusals, and the stored secret is worthless either way.
                store.Clear();
                Set(SidecarState.Stopped, exception.Message);
                return;
            }
            catch (Exception exception) when (exception is SidecarException or HttpRequestException)
            {
                // The server is down, or restarting, or the feature is off for a moment. The
                // credential is still believed good, so this recovers by itself.
                Set(SidecarState.Unreachable, exception.Message);
            }

            try
            {
                await delay(interval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Set(SidecarState state, string detail)
    {
        Status = new SessionStatus(state, detail);
        report?.Invoke(Status);
    }
}
