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

    private void Remove(int pid) => _byPid.TryRemove(pid, out _);

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
}
