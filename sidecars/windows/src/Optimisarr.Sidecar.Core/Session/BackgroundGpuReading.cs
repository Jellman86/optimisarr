namespace Optimisarr.Sidecar.Core.Session;

/// <summary>
/// The latest GPU busy figure, read off the caller's thread.
///
/// <para>A GPU read can take seconds — nvidia-smi is a process start, and the Windows counters
/// enumerate every engine of every process — while the tray's monitor request gives a snapshot
/// three seconds to return and a check-in should not wait on either. Callers get the cached figure
/// immediately; a stale one starts a single background refresh.</para>
///
/// <para>Shared across samplers, unlike CPU: a GPU figure describes the recent past whoever asks,
/// so there is no per-caller window to keep apart, and three samplers each polling the counters
/// would triple the cost for the same number.</para>
/// </summary>
public sealed class BackgroundGpuReading(
    Func<double?> read,
    TimeProvider? clock = null,
    TimeSpan? refreshInterval = null,
    Action<Action>? start = null)
{
    /// <summary>One for the whole process, reading counters first and nvidia-smi otherwise.</summary>
    public static BackgroundGpuReading Shared { get; } = new(GpuUtilisationSources.ReadComposite());

    private readonly Lock _gate = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly TimeSpan _interval = refreshInterval ?? TimeSpan.FromSeconds(5);
    private readonly Action<Action> _start = start ?? (work => _ = Task.Run(work));
    private double? _value;
    private DateTimeOffset? _readAt;
    private DateTimeOffset? _startedAt;
    private bool _refreshing;

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
            refresh = !_refreshing && (_startedAt is not { } started || now - started >= _interval);
            if (refresh)
            {
                _refreshing = true;
                _startedAt = now;
            }
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
