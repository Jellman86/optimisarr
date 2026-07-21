using System.Runtime.InteropServices;
using Optimisarr.Api.Realtime;

namespace Optimisarr.Api.Queue;

/// <summary>
/// Sends POSIX job-control signals to a process. Abstracted so the pause behaviour is unit
/// testable without real processes, and so platforms without <c>kill(2)</c> degrade cleanly.
/// </summary>
public interface IProcessSignals
{
    /// <summary>Whether this platform can suspend and resume processes at all.</summary>
    bool Supported { get; }

    bool TrySuspend(int pid);

    bool TryResume(int pid);
}

/// <summary>
/// Suspends and resumes processes with SIGSTOP/SIGCONT via libc's <c>kill(2)</c>. The container
/// image and WSL run Linux; macOS covers local development. Anywhere else reports unsupported,
/// and the pause degrades to only blocking new work.
/// </summary>
public sealed class PosixProcessSignals : IProcessSignals
{
    // The job-control signals are numbered differently per kernel: Linux SIGSTOP=19/SIGCONT=18,
    // macOS SIGSTOP=17/SIGCONT=19.
    private static readonly int? Sigstop = OperatingSystem.IsLinux() ? 19 : OperatingSystem.IsMacOS() ? 17 : null;
    private static readonly int? Sigcont = OperatingSystem.IsLinux() ? 18 : OperatingSystem.IsMacOS() ? 19 : null;

    public bool Supported => Sigstop is not null;

    public bool TrySuspend(int pid) => Sigstop is { } signal && Send(pid, signal);

    public bool TryResume(int pid) => Sigcont is { } signal && Send(pid, signal);

    // kill(2) treats pid 0 and negative pids as process-group broadcasts; only ever signal a
    // single, known process.
    private static bool Send(int pid, int signal) => pid > 0 && Kill(pid, signal) == 0;

    // DllImport rather than LibraryImport: the source-generated marshaller would force
    // AllowUnsafeBlocks onto the whole project for one int-only syscall.
    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int pid, int signal);
}

/// <summary>
/// The operator's manual pause switch for the queue. Pausing blocks new work from dispatching
/// (via <see cref="Optimisarr.Core.Scheduling.DispatchPolicyEvaluator"/>) and immediately
/// suspends the running transcode processes, so the server's CPU/GPU are freed for work
/// Optimisarr cannot detect — without losing encode progress. Resuming continues them in place.
/// A singleton shared by the dispatcher (which reports each ffmpeg it starts) and the pause
/// endpoints.
/// </summary>
public sealed class QueuePauseControl(
    ActiveEncodeRegistry encodes,
    IProcessSignals signals,
    ILogger<QueuePauseControl> logger)
{
    private readonly Lock _gate = new();
    private bool _paused;

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

    /// <summary>The dispatch-blocked reason shown while paused, honest about what pausing did.</summary>
    public string BlockedReason => signals.Supported
        ? "Paused by the operator — running encodes are suspended until the queue is resumed."
        : "Paused by the operator — running encodes will finish, and nothing new will start.";

    public void Pause()
    {
        lock (_gate)
        {
            if (_paused)
            {
                return;
            }

            _paused = true;
            SignalAll(suspend: true);
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (!_paused)
            {
                return;
            }

            _paused = false;
            SignalAll(suspend: false);
        }
    }

    /// <summary>
    /// Reported by the dispatcher right after it starts an ffmpeg process, so one that comes up
    /// while paused (e.g. claimed just before the pause landed) is suspended straight away
    /// instead of running until the next pause.
    /// </summary>
    public void OnEncodeStarted(int pid)
    {
        lock (_gate)
        {
            if (_paused)
            {
                Signal(pid, suspend: true);
            }
        }
    }

    private void SignalAll(bool suspend)
    {
        foreach (var pid in encodes.Pids)
        {
            Signal(pid, suspend);
        }
    }

    private void Signal(int pid, bool suspend)
    {
        if (!signals.Supported)
        {
            return;
        }

        // Best effort per process: one that exited between the click and the signal is simply
        // gone, and must not stop the others from being signalled.
        var delivered = suspend ? signals.TrySuspend(pid) : signals.TryResume(pid);
        if (!delivered)
        {
            logger.LogWarning(
                "Could not {Action} encode process {Pid}; it most likely already exited.",
                suspend ? "suspend" : "resume",
                pid);
        }
    }
}
