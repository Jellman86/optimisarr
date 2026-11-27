namespace Optimisarr.Sidecar.Core.Session;

/// <summary>
/// The latest GPU busy figure, read off the caller's thread.
///
/// <para>A GPU read can take seconds — nvidia-smi is a process start, and the Windows counters
/// enumerate every engine of every process — while the tray's monitor request gives a snapshot
/// three seconds to return and a check-in should not wait on either. Callers get the cached figure
/// immediately, and a timer keeps it fresh every refresh interval for as long as anyone is asking.
/// Refreshing only when asked would not do: check-ins come every 30 seconds and lease renewals
/// about every 15, so each would find the figure past its cutoff and report nothing at all.</para>
///
/// <para>Shared across samplers, unlike CPU: a GPU figure describes the recent past whoever asks,
/// so there is no per-caller window to keep apart, and three samplers each polling the counters
/// would triple the cost for the same number.</para>
/// </summary>
public sealed class BackgroundGpuReading(
    Func<double?> read,
    TimeProvider? clock = null,
    TimeSpan? refreshInterval = null,
    Action<Action>? start = null) : IDisposable
{
    /// <summary>One for the whole process, reading counters first and nvidia-smi otherwise.</summary>
    public static BackgroundGpuReading Shared { get; } = new(GpuUtilisationSources.ReadComposite());

    /// <summary>
    /// How long the timer keeps reading after the last caller. A paused or unpaired machine stops
    /// asking, and enumerating every GPU engine for nobody is wasted work.
    /// </summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(2);

    private readonly Lock _gate = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly TimeSpan _interval = refreshInterval ?? TimeSpan.FromSeconds(5);
    private readonly Action<Action> _start = start ?? (work => _ = Task.Run(work));
    private double? _value;
    private DateTimeOffset? _readAt;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset _askedAt;
    private bool _refreshing;
    private bool _disposed;
    private ITimer? _timer;

    /// <summary>
    /// The cached figure, or null before the first read completes or once it is older than three
    /// refresh intervals — a number from minutes ago would describe a different machine state.
    /// </summary>
    public double? Current()
    {
        var now = _clock.GetUtcNow();
        bool refresh;
        lock (_gate)
        {
            if (_disposed)
            {
                return null;
            }

            _askedAt = now;
            // Period and due time both the interval: this call already starts the first read.
            _timer ??= _clock.CreateTimer(_ => Tick(), null, _interval, _interval);
            refresh = TryBeginRefresh(now, onlyWhenStale: true);
        }

        if (refresh)
        {
            _start(Refresh);
        }

        lock (_gate)
        {
            return _readAt is { } readAt && _clock.GetUtcNow() - readAt <= _interval * 3 ? _value : null;
        }
    }

    public void Dispose()
    {
        ITimer? timer;
        lock (_gate)
        {
            _disposed = true;
            timer = _timer;
            _timer = null;
        }
        timer?.Dispose();
    }

    private void Tick()
    {
        var now = _clock.GetUtcNow();
        bool refresh;
        ITimer? idle = null;
        lock (_gate)
        {
            if (now - _askedAt >= IdleTimeout)
            {
                idle = _timer;
                _timer = null;
                refresh = false;
            }
            else
            {
                // The timer is the cadence, so it does not second-guess it against the clock: a
                // tick a few milliseconds early would otherwise skip a whole interval.
                refresh = TryBeginRefresh(now, onlyWhenStale: false);
            }
        }

        idle?.Dispose();
        if (refresh)
        {
            _start(Refresh);
        }
    }

    /// <summary>One read at a time: a hung read is left to finish rather than piled onto.</summary>
    private bool TryBeginRefresh(DateTimeOffset now, bool onlyWhenStale)
    {
        if (_disposed || _refreshing
            || (onlyWhenStale && _startedAt is { } started && now - started < _interval))
        {
            return false;
        }

        _refreshing = true;
        _startedAt = now;
        return true;
    }

    private void Refresh()
    {
        double? value;
        try
        {
            value = read();
        }
        catch (Exception)
        {
            // Nothing is awaiting this task, so an escaped exception would only be lost. Whatever
            // went wrong, the honest figure is "cannot say".
            value = null;
        }

        lock (_gate)
        {
            _value = value is { } fraction && double.IsFinite(fraction) ? Math.Clamp(fraction, 0, 1) : null;
            _readAt = _clock.GetUtcNow();
            _refreshing = false;
        }
    }
}
