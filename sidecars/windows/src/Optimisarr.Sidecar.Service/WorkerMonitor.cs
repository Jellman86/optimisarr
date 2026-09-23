using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Service;

public sealed class WorkerMonitor
{
    private readonly Lock _gate = new();
    private readonly Dictionary<int, MonitorJob> _jobs = [];
    private readonly Dictionary<int, byte[]> _previews = [];
    private readonly Func<DateTimeOffset> _now;
    private DateTimeOffset _previewRequested;
    private string? _last;

    public WorkerMonitor(Func<DateTimeOffset>? now = null) => _now = now ?? (() => DateTimeOffset.UtcNow);

    public bool WantsPreviews
    {
        get
        {
            lock (_gate)
            {
                if (_now() - _previewRequested < TimeSpan.FromSeconds(5)) return true;
                _previews.Clear();
                return false;
            }
        }
    }

    public void RequestPreviews()
    {
        lock (_gate)
        {
            var now = _now();
            if (now - _previewRequested >= TimeSpan.FromSeconds(5)) _previews.Clear();
            _previewRequested = now;
        }
    }
    public void EndPreviews() { lock (_gate) { _previewRequested = DateTimeOffset.MinValue; _previews.Clear(); } }

    public void Observe(MonitorJob job) { lock (_gate) { _jobs[job.JobId] = job; } }
    public void Remove(int jobId) { lock (_gate) { _jobs.Remove(jobId); _previews.Remove(jobId); } }
    public void Complete(int jobId, string detail)
    {
        lock (_gate) { _jobs.Remove(jobId); _previews.Remove(jobId); _last = $"Job #{jobId}: {detail}"; }
    }
    public bool PublishPreview(int jobId, byte[] jpeg)
    {
        if (jpeg.Length is < 4 or > MonitorProtocol.MaximumPreviewBytes
            || jpeg[0] != 0xff || jpeg[1] != 0xd8 || jpeg[^2] != 0xff || jpeg[^1] != 0xd9)
            return false;
        lock (_gate)
        {
            if (!_jobs.ContainsKey(jobId) || _now() - _previewRequested >= TimeSpan.FromSeconds(5)) return false;
            if (!_previews.ContainsKey(jobId) && _previews.Count >= MonitorProtocol.MaximumPreviewJobs) return false;
            _previews[jobId] = (byte[])jpeg.Clone();
            return true;
        }
    }

    public (MonitorJob[] Jobs, string? Last) Read(bool includePreviews = false)
    {
        lock (_gate)
        {
            var remaining = includePreviews ? MonitorProtocol.MaximumPreviewJobs : 0;
            MonitorJob WithPreview(MonitorJob job, byte[] jpeg)
            {
                remaining--;
                return job with { PreviewJpeg = jpeg };
            }
            var jobs = _jobs.Values.OrderBy(job => job.JobId)
                .Select(job => remaining > 0 && _previews.TryGetValue(job.JobId, out var jpeg)
                    ? WithPreview(job, jpeg) : job with { PreviewJpeg = null }).ToArray();
            return (jobs, _last);
        }
    }
}
