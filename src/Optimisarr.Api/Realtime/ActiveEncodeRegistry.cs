using System.Collections.Concurrent;

namespace Optimisarr.Api.Realtime;

/// <summary>
/// Tracks the ffmpeg processes Optimisarr currently has running, so the metrics broadcaster can
/// read their per-process GPU counters and the UI can tell whether work is hardware-accelerated.
/// A singleton shared between the queue dispatcher (which registers each ffmpeg it spawns) and
/// the broadcaster.
/// </summary>
public sealed class ActiveEncodeRegistry
{
    // pid -> whether the encode uses a hardware (GPU) video encoder.
    private readonly ConcurrentDictionary<int, bool> _byPid = new();

    // Verification (VMAF) runs its own ffmpeg outside this registry, but it is still active,
    // CPU-heavy work. Count it separately so the metrics broadcaster keeps sampling the CPU while
    // a job verifies, even though no encode process is registered.
    private int _verifications;

    /// <summary>
    /// Registers a running ffmpeg process. Dispose the returned token (in a <c>finally</c>) to
    /// deregister it when the process exits.
    /// </summary>
    public IDisposable Track(int pid, bool hardwareEncoder)
    {
        _byPid[pid] = hardwareEncoder;
        return new Registration(this, pid);
    }

    public IReadOnlyCollection<int> Pids => _byPid.Keys.ToArray();

    public int Count => _byPid.Count;

    /// <summary>True when any running encode is using a hardware video encoder.</summary>
    public bool AnyHardware => _byPid.Values.Any(hardware => hardware);

    /// <summary>True while a job is being verified (VMAF), even if no encode process is registered.</summary>
    public bool VerificationInProgress => Volatile.Read(ref _verifications) > 0;

    /// <summary>
    /// Marks a verification as in progress. Dispose the returned token (in a <c>finally</c>) when
    /// it finishes, so the metrics broadcaster keeps reporting CPU load for the duration.
    /// </summary>
    public IDisposable TrackVerification()
    {
        Interlocked.Increment(ref _verifications);
        return new VerificationRegistration(this);
    }

    private void Remove(int pid) => _byPid.TryRemove(pid, out _);

    private void EndVerification() => Interlocked.Decrement(ref _verifications);

    private sealed class Registration(ActiveEncodeRegistry registry, int pid) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            registry.Remove(pid);
        }
    }

    private sealed class VerificationRegistration(ActiveEncodeRegistry registry) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            registry.EndVerification();
        }
    }
}
