using Optimisarr.Core.Workers;
using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Linux;

/// <summary>What the dashboard shows about pairing. The configured server is its public form only.</summary>
public sealed record PairingView(bool Required, string? ConfiguredServer, bool InProgress, string? Problem);

public sealed record PairingAttempt(int StatusCode, string? Problem);

/// <summary>
/// Lets the dashboard pair a worker that has no credential, so a container needs no one-time code
/// in its environment. Open only while the host is waiting for a pairing, one attempt at a time,
/// and closed for good by the first success: an already-paired worker cannot be re-pointed at
/// another server from a page anyone on the network can open.
/// </summary>
public sealed class PairingDesk
{
    private sealed record Waiter(string? ConfiguredServer, Func<string, string, CancellationToken, Task<StoredPairing>> Pair,
        TaskCompletionSource<StoredPairing> Done, CancellationToken Lifetime);

    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _single = new(1, 1);
    private Waiter? _waiter;
    private string? _problem;

    public PairingView View
    {
        get
        {
            lock (_gate)
                return new(_waiter is not null, MonitorProtocol.PublicServerAddress(_waiter?.ConfiguredServer),
                    _waiter is not null && _single.CurrentCount == 0, _waiter is null ? null : _problem);
        }
    }

    /// <summary>
    /// Waits until the page pairs this worker. A configured server wins over whatever the page sends:
    /// the host refuses to start against a pairing for a different server than it was configured with.
    /// </summary>
    public async Task<StoredPairing> WaitAsync(string? configuredServer, string? problem,
        Func<string, string, CancellationToken, Task<StoredPairing>> pair, CancellationToken token)
    {
        var waiter = new Waiter(configuredServer, pair,
            new TaskCompletionSource<StoredPairing>(TaskCreationOptions.RunContinuationsAsynchronously), token);
        lock (_gate)
        {
            _waiter = waiter;
            _problem = problem;
        }
        try { return await waiter.Done.Task.WaitAsync(token); }
        finally
        {
            lock (_gate)
                if (_waiter == waiter) _waiter = null;
        }
    }

    public async Task<PairingAttempt> SubmitAsync(string? server, string? code)
    {
        Waiter? waiter;
        lock (_gate) waiter = _waiter;
        if (waiter is null) return new(StatusCodes.Status409Conflict, "This worker is already paired.");

        var address = waiter.ConfiguredServer ?? server?.Trim();
        if (!WorkerOptions.IsServerAddress(address))
            return new(StatusCodes.Status400BadRequest,
                "Enter the address you open Optimisarr at, starting with http:// or https://.");
        // Spaces and dashes are how people read a code aloud; the server accepts them too.
        var digits = string.Concat((code ?? "").Where(c => !char.IsWhiteSpace(c) && c != '-'));
        if (digits.Length != PairingCode.Digits || !digits.All(char.IsAsciiDigit))
            return new(StatusCodes.Status400BadRequest, $"A pairing code is {PairingCode.Digits} digits.");

        if (!await _single.WaitAsync(0)) return new(StatusCodes.Status409Conflict, "A pairing attempt is already running.");
        try
        {
            lock (_gate)
                if (_waiter != waiter || waiter.Done.Task.IsCompleted)
                    return new(StatusCodes.Status409Conflict, "This worker is already paired.");
            // The host's lifetime, not the request's: a closed browser tab must not abandon a
            // credential the server has already issued and will never issue again.
            var pairing = await waiter.Pair(address!, digits, waiter.Lifetime);
            waiter.Done.TrySetResult(pairing);
            return new(StatusCodes.Status200OK, null);
        }
        catch (SidecarException error) { return Refused(StatusCodes.Status400BadRequest, error.Message); }
        catch (HttpRequestException)
        {
            return Refused(StatusCodes.Status502BadGateway,
                "Could not reach that server from this worker. Check the address and that this machine can reach it.");
        }
        catch (TaskCanceledException) when (!waiter.Lifetime.IsCancellationRequested)
        {
            return Refused(StatusCodes.Status504GatewayTimeout, "The server did not answer in time. Try again.");
        }
        finally { _single.Release(); }
    }

    private PairingAttempt Refused(int status, string problem)
    {
        lock (_gate) _problem = problem;
        return new(status, problem);
    }
}
