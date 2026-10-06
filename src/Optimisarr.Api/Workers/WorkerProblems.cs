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

    private const string VerificationFailedPrefix = "Verification failed: ";

    /// <summary>
    /// A delivered candidate the server rejected. The job's own error already opens with
    /// "Verification failed:", so it is dropped rather than said twice, and the file is named by
    /// its leaf because the card has room for one line.
    /// </summary>
    public static string FailedVerification(string? relativePath, int jobId, string? errorMessage)
    {
        var file = string.IsNullOrEmpty(relativePath) ? $"job {jobId}" : Path.GetFileName(relativePath);
        var reason = string.IsNullOrWhiteSpace(errorMessage)
            ? "no reason recorded."
            : errorMessage.StartsWith(VerificationFailedPrefix, StringComparison.Ordinal)
                ? errorMessage[VerificationFailedPrefix.Length..]
                : errorMessage;
        return $"Its candidate for {file} failed verification: {reason.TrimEnd('.')}.";
    }
}
