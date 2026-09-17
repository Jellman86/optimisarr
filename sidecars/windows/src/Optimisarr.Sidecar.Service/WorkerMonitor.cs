using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Service;

public sealed class WorkerMonitor
{
    private readonly Lock _gate = new();
    private readonly Dictionary<int, MonitorJob> _jobs = [];
    private string? _last;

    public void Observe(MonitorJob job) { lock (_gate) { _jobs[job.JobId] = job; } }
    public void Remove(int jobId) { lock (_gate) { _jobs.Remove(jobId); } }
    public void Complete(int jobId, string detail)
    {
        lock (_gate) { _jobs.Remove(jobId); _last = $"Job #{jobId}: {detail}"; }
    }
    public (MonitorJob[] Jobs, string? Last) Read()
    {
        lock (_gate) { return ([.. _jobs.Values.OrderBy(job => job.JobId)], _last); }
    }
}
