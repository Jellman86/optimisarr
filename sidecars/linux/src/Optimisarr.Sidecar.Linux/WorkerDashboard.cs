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
    string? HardwareDecoder, long PreviewRevision, bool HasArtwork, DateTimeOffset? StartedAt);

public sealed record FinishedJob(int JobId, string Title, bool Delivered, DateTimeOffset FinishedAt);

public sealed record UpdateNotice(string Version, string ReleaseUrl);

public sealed record DashboardSnapshot(string Name, string State, string? ServerAddress,
    string ScratchPath, ScratchStorage Storage, int Concurrency, SidecarCapabilities? Capabilities,
    IReadOnlyList<DashboardJob> Jobs, IReadOnlyList<FinishedJob> Recent, string Version, WorkerMetrics? Metrics,
    string BrandStyle, PairingView Pairing, UpdateNotice? Update);

// Deliberately excludes pairing credentials and raw exception text. Previews and artwork use bounded separate endpoints.
public sealed class WorkerDashboard(string name, string? serverAddress, string scratchPath, int concurrency, Func<WorkerMetrics>? metrics = null)
{
    private const int RecentLimit = 5;
    private static readonly HashSet<string> ServableImages = ["image/jpeg", "image/png", "image/webp"];

    private readonly Lock _gate = new();
    private readonly Dictionary<int, MonitorJob> _jobs = [];
    private readonly Dictionary<int, (Assignment Assignment, DateTimeOffset StartedAt)> _assignments = [];
    private readonly Dictionary<int, (byte[] Bytes, long Revision)> _previews = [];
    private readonly Dictionary<int, LeaseArtwork> _artwork = [];
    private readonly HashSet<int> _finished = [];
    private readonly LinkedList<FinishedJob> _recent = [];
    private long _lastViewer = -30_000;
    private long _previewRevision;
    private string _state = "Starting";
    private string? _serverAddress = serverAddress;
    private string _brandStyle = "precession";
    private UpdateNotice? _update;
    private SidecarCapabilities? _capabilities;

    public PairingDesk Pairing { get; } = new();
    public string ScratchPath => scratchPath;
    public bool WantsPreview => Environment.TickCount64 - Interlocked.Read(ref _lastViewer) < 12_000;
    public void Viewed() => Interlocked.Exchange(ref _lastViewer, Environment.TickCount64);

    public void Start(Assignment assignment)
    {
        lock (_gate)
        {
            _finished.Remove(assignment.JobId);
            _assignments[assignment.JobId] = (assignment, DateTimeOffset.UtcNow);
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

    /// <summary>Kept only for a job still running here, and only as a type the page may serve as an image.</summary>
    public void Artwork(int jobId, LeaseArtwork artwork)
    {
        if (artwork.Bytes.Length is 0 || !ServableImages.Contains(artwork.ContentType.ToLowerInvariant())) return;
        lock (_gate)
            if (_assignments.ContainsKey(jobId)) _artwork[jobId] = artwork with { ContentType = artwork.ContentType.ToLowerInvariant() };
    }

    public LeaseArtwork? ReadArtwork(int jobId)
    {
        lock (_gate) return _artwork.GetValueOrDefault(jobId);
    }

    public void Capabilities(SidecarCapabilities capabilities) { lock (_gate) _capabilities = capabilities; }

    public void Status(SessionStatus status, bool draining = false)
    {
        lock (_gate) _state = draining && status.State is SidecarState.Connected or SidecarState.Working
            ? "Draining" : status.State.ToString();
    }

    public void Paired(string serverAddress) { lock (_gate) _serverAddress = serverAddress; }

    /// <summary>What the server said on its latest check-in that this page shows.</summary>
    public void Server(string? brandStyle, SidecarUpdate? update)
    {
        lock (_gate)
        {
            if (brandStyle is "precession" or "stellar") _brandStyle = brandStyle;
            _update = update is null ? null : new(update.Version, update.ReleasePage.AbsoluteUri);
        }
    }

    public void Observe(MonitorJob job)
    {
        lock (_gate)
            if (!_finished.Contains(job.JobId)) _jobs[job.JobId] = job with { PreviewJpeg = null };
    }

    public void Finish(int jobId, bool delivered)
    {
        lock (_gate)
        {
            var title = _jobs.GetValueOrDefault(jobId)?.Title
                ?? (_assignments.TryGetValue(jobId, out var started) ? started.Assignment.Title : $"Job {jobId}");
            _jobs.Remove(jobId);
            _assignments.Remove(jobId);
            _previews.Remove(jobId);
            _artwork.Remove(jobId);
            _finished.Add(jobId);
            // A process-local guard for queued progress callbacks, not an unbounded job history.
            if (_finished.Count > 256) _finished.Remove(_finished.First());
            _recent.AddFirst(new FinishedJob(jobId, title, delivered, DateTimeOffset.UtcNow));
            while (_recent.Count > RecentLimit) _recent.RemoveLast();
        }
    }

    public DashboardSnapshot Snapshot(ScratchStorage storage)
    {
        var pairing = Pairing.View;
        lock (_gate)
        {
            var state = pairing.Required ? "Unpaired" : _state == "Connected" && _jobs.Count > 0 ? "Working" : _state;
            return new(name, state, MonitorProtocol.PublicServerAddress(_serverAddress), scratchPath,
                storage, concurrency, _capabilities, [.. _jobs.Values.Select(JobSnapshot)], [.. _recent],
                SidecarBuild.Version, metrics?.Invoke(), _brandStyle, pairing, _update);
        }
    }

    private DashboardJob JobSnapshot(MonitorJob job)
    {
        var assignment = _assignments.TryGetValue(job.JobId, out var started) ? started.Assignment : null;
        string? decoder = null;
        if (assignment is not null)
            for (var i = 0; i + 1 < assignment.Arguments.Count; i++)
                if (assignment.Arguments[i] == "-hwaccel") decoder = assignment.Arguments[i + 1];
        return new(job.JobId, job.Title, job.Encoder, job.Stage, job.EncodedSeconds,
            assignment?.SourceBytes, assignment?.SourceMedia, assignment?.OutputExtension, decoder,
            _previews.GetValueOrDefault(job.JobId).Revision, _artwork.ContainsKey(job.JobId),
            assignment is null ? null : started.StartedAt);
    }
}
