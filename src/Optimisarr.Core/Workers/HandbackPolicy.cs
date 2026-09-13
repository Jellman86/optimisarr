namespace Optimisarr.Core.Workers;

/// <summary>
/// Whether a job may be offered to a worker that has already given it back.
///
/// A released job goes straight back on the queue, and the claim loop offers the highest-priority
/// queued job to whoever asks. Those two together are a loop: a worker that cannot run a job hands
/// it back, is offered it again on its next check-in, and repeats forever. That is not a
/// theoretical cost — each attempt downloads the source again, which for a film is gigabytes over
/// and over.
///
/// Neither extreme works. Offering it again immediately is the loop. Never offering it again
/// punishes the ordinary case, a Mac that went to sleep mid-job, by permanently excluding a
/// perfectly good worker from one file. So a handback earns a pause, and a job that keeps coming
/// back stops being offered to workers at all, which leaves it for this server to run and leaves
/// the reason visible on the worker's card.
/// </summary>
public static class HandbackPolicy
{
    /// <summary>
    /// How long a worker is left alone after giving a job back. Comfortably longer than the
    /// check-in interval, so a failing job is retried in minutes rather than seconds, and short
    /// enough that a laptop which woke up is useful again quickly.
    /// </summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How many handbacks a job may collect before workers stop being offered it. Three separate
    /// refusals is no longer bad luck.
    /// </summary>
    public const int MaxHandbacks = 3;

    /// <summary>
    /// Whether this worker may be offered this job now, given when it last handed it back and how
    /// many times the job has been handed back by anyone.
    /// </summary>
    public static bool MayOffer(DateTimeOffset? lastHandbackByThisWorker, int handbacksByAnyone, DateTimeOffset now)
    {
        if (handbacksByAnyone >= MaxHandbacks)
        {
            return false;
        }

        return lastHandbackByThisWorker is not { } handback || now - handback >= Cooldown;
    }

    /// <summary>Why the job is being held back, for the log and the worker's card.</summary>
    public static string Explain(DateTimeOffset? lastHandbackByThisWorker, int handbacksByAnyone, DateTimeOffset now)
    {
        if (handbacksByAnyone >= MaxHandbacks)
        {
            return $"It has been handed back {handbacksByAnyone} times and is no longer offered to workers.";
        }

        if (lastHandbackByThisWorker is { } handback)
        {
            var wait = Cooldown - (now - handback);
            return $"This worker handed it back; it will not be offered again for {wait.TotalMinutes:F0} more minutes.";
        }

        return "It may be offered.";
    }
}
