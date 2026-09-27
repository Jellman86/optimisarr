using Optimisarr.Sidecar.Core.Capabilities;
using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Linux;

public sealed record ScratchStorage(string Kind, long FreeBytes, long TotalBytes)
{
    public static ScratchStorage Read(string path)
    {
        var drive = new DriveInfo(Path.GetFullPath(path));
        return new(drive.DriveFormat is "tmpfs" or "ramfs" ? "RAM" : "Disk",
            drive.AvailableFreeSpace, drive.TotalSize);
    }
}

public sealed record DashboardSnapshot(string Name, string State, string? ServerAddress,
    string ScratchPath, ScratchStorage Storage, int Concurrency, SidecarCapabilities? Capabilities,
    IReadOnlyList<MonitorJob> Jobs, string? LastOutcome, string Version);

// Deliberately excludes pairing credentials, raw exception text and media previews.
public sealed class WorkerDashboard(string name, string? serverAddress, string scratchPath, int concurrency)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<int, MonitorJob> _jobs = [];
    private string _state = "Starting";
    private string? _lastOutcome;
    private SidecarCapabilities? _capabilities;
    public string ScratchPath => scratchPath;

    public void Capabilities(SidecarCapabilities capabilities) { lock (_gate) _capabilities = capabilities; }
    public void Status(SessionStatus status, bool draining = false)
    {
        lock (_gate) _state = draining && status.State is SidecarState.Connected or SidecarState.Working
            ? "Draining" : status.State.ToString();
    }
    public void Observe(MonitorJob job) { lock (_gate) _jobs[job.JobId] = job with { PreviewJpeg = null }; }
    public void Finish(int jobId, bool succeeded)
    {
        lock (_gate)
        {
            _jobs.Remove(jobId);
            _lastOutcome = $"Job {jobId} {(succeeded ? "completed" : "ended without a replacement")}";
        }
    }
    public DashboardSnapshot Snapshot(ScratchStorage storage)
    {
        lock (_gate)
            return new(name, _state, MonitorProtocol.PublicServerAddress(serverAddress), scratchPath,
                storage, concurrency, _capabilities, [.. _jobs.Values], _lastOutcome,
                typeof(WorkerDashboard).Assembly.GetName().Version?.ToString(3) ?? "unknown");
    }
}
