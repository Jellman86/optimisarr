namespace Optimisarr.Sidecar.Core.Session;

/// <summary>Volatile, cancelable shutdown intent. No state survives a worker restart.</summary>
public sealed class ShutdownCountdown
{
    private DateTimeOffset? _readySince;
    private bool _initiated;
    private string? _failure;

    public bool Armed { get; private set; }
    public bool CanCancel => Armed && (!_initiated || _failure is not null);
    public string Detail { get; private set; } = "";
    public int? SecondsRemaining(DateTimeOffset now) =>
        Armed && _readySince is { } since && !_initiated
            ? Math.Max(0, (int)Math.Ceiling((since.AddSeconds(60) - now).TotalSeconds))
            : null;

    public void Arm()
    {
        if (Armed) return;
        Armed = true;
        _readySince = null;
        _initiated = false;
        _failure = null;
        Detail = "Waiting for the server to confirm draining";
    }

    public void Cancel()
    {
        if (!CanCancel) return;
        Armed = false;
        _readySince = null;
        _initiated = false;
        _failure = null;
        Detail = "";
    }

    public bool Evaluate(DateTimeOffset now, bool ready, int activeJobs, bool unacknowledged = false,
        bool serverUnreachable = false)
    {
        if (!Armed || _initiated || _failure is not null) return false;
        if (serverUnreachable)
        {
            _readySince = null;
            Detail = "Server unreachable; shutdown is blocked until check-ins recover"
                + (activeJobs > 0 ? " and held work is acknowledged." : ".");
            return false;
        }
        if (activeJobs > 0)
        {
            _readySince = null;
            Detail = $"Waiting for {activeJobs} job{(activeJobs == 1 ? "" : "s")} to finish verification, return, and acknowledgement";
            return false;
        }
        if (unacknowledged)
        {
            _readySince = null;
            Detail = "A job result was not acknowledged by the server. Inspect diagnostics; shutdown is blocked.";
            return false;
        }
        if (!ready)
        {
            _readySince = null;
            Detail = "Waiting for the server to confirm draining";
            return false;
        }
        _readySince ??= now;
        var remaining = SecondsRemaining(now);
        Detail = $"No jobs held. Shutting down in {remaining} seconds; cancel at any time.";
        if (remaining > 0) return false;
        Detail = "Confirming the server before shutdown; cancel is still available.";
        return true;
    }

    public bool Begin()
    {
        if (!CanCancel || _failure is not null || _readySince is null) return false;
        _initiated = true;
        Detail = "Asking Windows to shut down";
        return true;
    }

    public void Defer(string reason)
    {
        if (!CanCancel) return;
        _readySince = null;
        Detail = reason;
    }

    public void Fail(string reason)
    {
        _failure = reason;
        Detail = reason;
    }
}
