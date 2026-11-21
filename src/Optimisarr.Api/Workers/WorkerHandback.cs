using Optimisarr.Api.Queue;
using Optimisarr.Core.Queue;
using Optimisarr.Data;

namespace Optimisarr.Api.Workers;

/// <summary>
/// Keeps a worker's account of why it handed a job back. Before this a release carried no reason,
/// so on 2026-11-09 Boothby failed two episodes the same way 20 times each and the job showed
/// nothing until the server ran it itself. The attempt is now archived with the worker's report,
/// and a failure this server knows how to route around changes the next attempt's plan.
/// </summary>
internal static class WorkerHandback
{
    public const string AttemptReason = "WorkerHandback";

    public static void Record(Job job, JobLease lease, string? workerName, string failure, DateTimeOffset nowUtc)
    {
        lease.HandbackFailure = failure;
        var qsvStopped = IntelQsvTimestampFallback.Applies(job.VideoEncoder, failure);
        var activeLog = job.ProcessLog;
        job.ProcessLog = failure;
        JobAttemptHistory.Archive(job, qsvStopped ? IntelQsvTimestampFallback.AttemptReason : AttemptReason,
            nowUtc, workerName, lease.HardwareDecoder, "Failed");
        job.ProcessLog = activeLog;
        if (qsvStopped)
        {
            job.AvoidIntelQsv = true;
            job.RetryReason = IntelQsvTimestampFallback.RetryReason;
        }
    }
}
