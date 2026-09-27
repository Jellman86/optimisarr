using Optimisarr.Sidecar.Core.Capabilities;
using WorkerMediaInfo = Optimisarr.Core.Workers.WorkerMediaInfo;
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

public sealed record DashboardJob(int JobId, string Title, string Encoder, RemoteStage Stage,
    double? EncodedSeconds, long? SourceBytes, WorkerMediaInfo? SourceMedia, string? OutputExtension,
    string? HardwareDecoder, long PreviewRevision);

public sealed record DashboardSnapshot(string Name, string State, string? ServerAddress,
    string ScratchPath, ScratchStorage Storage, int Concurrency, SidecarCapabilities? Capabilities,
    IReadOnlyList<DashboardJob> Jobs, string? LastOutcome, string Version, WorkerMetrics? Metrics);

// Deliberately excludes pairing credentials and raw exception text. Previews use a bounded separate endpoint.
public sealed class WorkerDashboard(string name, string? serverAddress, string scratchPath, int concurrency, Func<WorkerMetrics>? metrics = null)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<int, MonitorJob> _jobs = [];
    private readonly Dictionary<int, Assignment> _assignments = [];
    private readonly Dictionary<int, (byte[] Bytes, long Revision)> _previews = [];
    private readonly HashSet<int> _finished = [];
    private long _lastViewer = -30_000;
    private long _previewRevision;
    public bool WantsPreview => Environment.TickCount64 - Interlocked.Read(ref _lastViewer) < 12_000;
    public void Viewed() => Interlocked.Exchange(ref _lastViewer, Environment.TickCount64);
    public void Start(Assignment assignment)
    {
        lock (_gate)
        {
            _finished.Remove(assignment.JobId);
            _assignments[assignment.JobId] = assignment;
        }
    }
    public void Preview(int jobId, byte[] jpeg)
    {
        if (jpeg.Length is 0 or > MonitorProtocol.MaximumPreviewBytes) return;
        lock (_gate)
            if (_jobs.ContainsKey(jobId)) _previews[jobId] = (jpeg.ToArray(), ++_previewRevision);
    }
    public byte[]? ReadPreview(int jobId)
    {
        lock (_gate) return _previews.TryGetValue(jobId, out var preview) ? preview.Bytes : null;
    }
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
    public void Observe(MonitorJob job)
    {
        lock (_gate)
            if (!_finished.Contains(job.JobId)) _jobs[job.JobId] = job with { PreviewJpeg = null };
    }
    public void Finish(int jobId, bool succeeded)
    {
        lock (_gate)
        {
            _jobs.Remove(jobId);
            _assignments.Remove(jobId);
            _previews.Remove(jobId);
            _finished.Add(jobId);
            // A process-local guard for queued progress callbacks, not an unbounded job history.
            if (_finished.Count > 256) _finished.Remove(_finished.First());
            _lastOutcome = $"Job {jobId} {(succeeded ? "returned its result to the server" : "stopped; check the main server for its result")}";
        }
    }
    public DashboardSnapshot Snapshot(ScratchStorage storage)
    {
        lock (_gate)
            return new(name, _state == "Connected" && _jobs.Count > 0 ? "Working" : _state, MonitorProtocol.PublicServerAddress(serverAddress), scratchPath,
                storage, concurrency, _capabilities, [.. _jobs.Values.Select(JobSnapshot)], _lastOutcome,
                typeof(WorkerDashboard).Assembly.GetName().Version?.ToString(3) ?? "unknown", metrics?.Invoke());
    }
    private DashboardJob JobSnapshot(MonitorJob job)
    {
        _assignments.TryGetValue(job.JobId, out var assignment);
        string? decoder = null;
        if (assignment is not null)
            for (var i = 0; i + 1 < assignment.Arguments.Count; i++)
                if (assignment.Arguments[i] == "-hwaccel") decoder = assignment.Arguments[i + 1];
        return new(job.JobId, job.Title, job.Encoder, job.Stage, job.EncodedSeconds,
            assignment?.SourceBytes, assignment?.SourceMedia, assignment?.OutputExtension, decoder,
            _previews.GetValueOrDefault(job.JobId).Revision);
    }
}
