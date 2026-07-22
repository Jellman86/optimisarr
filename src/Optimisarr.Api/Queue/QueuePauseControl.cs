using System.Runtime.InteropServices;
using Optimisarr.Api.Realtime;

namespace Optimisarr.Api.Queue;

public interface IProcessSignals
{
    bool Supported { get; }

    bool TrySuspend(int pid);

    bool TryResume(int pid);
}

/// <summary>Sends POSIX job-control signals to a single known child process.</summary>
public sealed class PosixProcessSignals : IProcessSignals
{
    private static readonly int? Sigstop = OperatingSystem.IsLinux() ? 19 : OperatingSystem.IsMacOS() ? 17 : null;
    private static readonly int? Sigcont = OperatingSystem.IsLinux() ? 18 : OperatingSystem.IsMacOS() ? 19 : null;

    public bool Supported => Sigstop is not null;

    public bool TrySuspend(int pid) => Sigstop is { } signal && Send(pid, signal);

    public bool TryResume(int pid) => Sigcont is { } signal && Send(pid, signal);

    // kill(2) treats zero and negative pids as process-group broadcasts. Only signal the exact
    // positive pid registered immediately after Optimisarr starts its own ffmpeg child.
    private static bool Send(int pid, int signal) => pid > 0 && Kill(pid, signal) == 0;

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int pid, int signal);
}

public sealed record QueueResumeResult(bool Resumed, int FailedEncodeCount)
{
    public static QueueResumeResult Success { get; } = new(true, 0);
}

public sealed record QueuePauseSnapshot(
    bool IsPaused,
    string Mode,
    bool RunningEncodesSuspended,
    int RunningEncodeCount,
    int SuspendedEncodeCount,
    int FailedEncodeCount,
    string? BlockedReason);

/// <summary>
/// Owns the in-memory manual-pause state and the exact set of ffmpeg children that Optimisarr
/// successfully suspended. Dispatch is not reopened until every still-running suspended child
/// accepts SIGCONT; failures remain visible and retryable instead of stranding an encode silently.
/// </summary>
public sealed class QueuePauseControl(
    ActiveEncodeRegistry encodes,
    IProcessSignals signals,
    ILogger<QueuePauseControl> logger)
{
    private readonly Lock _gate = new();
    private readonly HashSet<int> _suspendedPids = [];
    private bool _paused;
    private bool _resumeFailed;

    public bool IsPaused
    {
        get
        {
            lock (_gate)
            {
                return _paused;
            }
        }
    }

    public QueuePauseSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return BuildSnapshot();
            }
        }
    }

    public string? BlockedReason => Snapshot.BlockedReason;

    public void Pause()
    {
        lock (_gate)
        {
            if (_paused)
            {
                return;
            }

            _paused = true;
            _resumeFailed = false;
            foreach (var pid in encodes.Pids)
            {
                TrySuspend(pid);
            }
        }
    }

    public QueueResumeResult Resume() => ResumeCore(reopenDispatch: true);

    /// <summary>Lets a stopping host drain children without clearing the durable dispatch pause.</summary>
    public QueueResumeResult ResumeProcessesForShutdown() => ResumeCore(reopenDispatch: false);

    private QueueResumeResult ResumeCore(bool reopenDispatch)
    {
        lock (_gate)
        {
            if (!_paused)
            {
                return QueueResumeResult.Success;
            }

            if (!signals.Supported)
            {
                _paused = !reopenDispatch;
                _resumeFailed = false;
                _suspendedPids.Clear();
                return QueueResumeResult.Success;
            }

            var active = encodes.Pids.ToHashSet();
            _suspendedPids.RemoveWhere(pid => !active.Contains(pid));
            var failed = 0;
            foreach (var pid in _suspendedPids.ToArray())
            {
                if (TryResume(pid) || !encodes.Pids.Contains(pid))
                {
                    _suspendedPids.Remove(pid);
                    continue;
                }

                failed++;
                logger.LogWarning(
                    "Could not resume encode process {Pid}; queue dispatch remains paused so the operator can retry.",
                    pid);
            }

            if (failed > 0)
            {
                _resumeFailed = true;
                return new QueueResumeResult(false, failed);
            }

            _paused = !reopenDispatch;
            _resumeFailed = false;
            _suspendedPids.Clear();
            return QueueResumeResult.Success;
        }
    }

    /// <summary>Closes the start-versus-pause race for an ffmpeg child claimed just beforehand.</summary>
    public void OnEncodeStarted(int pid)
    {
        lock (_gate)
        {
            if (_paused)
            {
                // A long pause can outlive a child and the OS may reuse its numeric pid. This
                // callback represents a new process generation, so never trust an old delivery.
                _suspendedPids.Remove(pid);
                TrySuspend(pid);
            }
        }
    }

    private void TrySuspend(int pid)
    {
        if (!signals.Supported || _suspendedPids.Contains(pid))
        {
            return;
        }

        bool delivered;
        try
        {
            delivered = signals.TrySuspend(pid);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not signal encode process {Pid} to suspend.", pid);
            return;
        }

        if (delivered)
        {
            _suspendedPids.Add(pid);
            return;
        }

        logger.LogWarning(
            "Could not suspend encode process {Pid}; it may have exited or rejected the signal.",
            pid);
    }

    private bool TryResume(int pid)
    {
        try
        {
            return signals.TryResume(pid);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not signal encode process {Pid} to resume.", pid);
            return false;
        }
    }

    private QueuePauseSnapshot BuildSnapshot()
    {
        if (!_paused)
        {
            return new QueuePauseSnapshot(false, "inactive", false, encodes.Count, 0, 0, null);
        }

        var active = encodes.Pids.ToHashSet();
        var suspended = _suspendedPids.Count(active.Contains);
        var failed = Math.Max(0, active.Count - suspended);
        var verificationNote = encodes.VerificationInProgress
            ? " Verification already in progress will finish."
            : string.Empty;

        if (!signals.Supported)
        {
            return new QueuePauseSnapshot(
                true,
                "dispatchOnly",
                false,
                active.Count,
                0,
                active.Count,
                "Paused by the operator — running encodes will finish because process suspension is unavailable; nothing new will start."
                + verificationNote);
        }

        if (_resumeFailed)
        {
            var resumeFailures = Math.Max(1, suspended);
            return new QueuePauseSnapshot(
                true,
                "partial",
                false,
                active.Count,
                suspended,
                resumeFailures,
                $"Queue dispatch remains paused because {resumeFailures} running encode(s) could not be resumed. Retry Resume queue."
                + verificationNote);
        }

        if (failed > 0)
        {
            return new QueuePauseSnapshot(
                true,
                "partial",
                false,
                active.Count,
                suspended,
                failed,
                $"Queue dispatch is paused, but {failed} of {active.Count} running encode(s) could not be suspended and may still be working."
                + verificationNote);
        }

        if (active.Count == 0)
        {
            return new QueuePauseSnapshot(
                true,
                "suspended",
                false,
                0,
                0,
                0,
                "Paused by the operator — nothing new will start." + verificationNote);
        }

        return new QueuePauseSnapshot(
            true,
            "suspended",
            active.Count > 0,
            active.Count,
            suspended,
            0,
            "Paused by the operator — running encodes are suspended until the queue is resumed."
            + verificationNote);
    }
}
