using Optimisarr.Data;

namespace Optimisarr.Api.Queue;

/// <summary>
/// Keeps failed interactive comparison rows as a small diagnostic audit while their disposable
/// media is removed. Successful, cancelled, and active comparison rows remain throwaway.
/// </summary>
internal static class DiagnosticJobRetention
{
    public static bool ShouldRetain(JobType type, JobStatus status) =>
        type != JobType.Normal && status == JobStatus.Failed;
}
