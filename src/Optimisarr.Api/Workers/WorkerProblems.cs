using Optimisarr.Data;

namespace Optimisarr.Api.Workers;

/// <summary>
/// The one line an operator sees under "Last problem" on a worker. Recorded where the server
/// refuses or discards something the worker did — a lapsed lease, a candidate encoded from the
/// wrong bytes, a delivered candidate that failed verification — because those are the cases an
/// operator can act on, and the worker itself may never learn of them.
/// </summary>
internal static class WorkerProblems
{
    public const int MaxLength = 512;

    public static void Record(Worker worker, string message, DateTimeOffset nowUtc)
    {
        worker.LastProblem = message.Length > MaxLength ? message[..MaxLength] : message;
        worker.LastProblemAt = nowUtc;
    }
}
