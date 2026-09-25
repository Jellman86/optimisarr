namespace Optimisarr.Core.Queue;

/// <summary>
/// Why an encode was judged stalled, so the failure reason can say what was actually observed.
/// </summary>
public enum EncodeStallKind
{
    /// <summary>
    /// FFmpeg wrote its final <c>progress=end</c> block, so the output is complete, but the
    /// process did not exit afterwards. Seen in the wild as a job frozen at 100% that only a
    /// container restart cleared; on a healthy encode the final block and the exit are the same
    /// instant.
    /// </summary>
    DidNotExitAfterFinalReport,

    /// <summary>
    /// FFmpeg stopped reporting progress mid-encode for longer than any real frame takes. A
    /// paused encode is never counted: SIGSTOP silences it on purpose.
    /// </summary>
    NoProgressReported
}

/// <summary>
/// Decides, from timestamps alone, whether a running FFmpeg has stopped making progress. Pure so
/// it is testable without a process: the runner feeds it every progress line and asks it on a
/// timer. Its answer turns an encode that would otherwise hold a queue slot forever into a
/// failed job with a reason, which the retry and exclusion policies already know how to handle.
/// </summary>
public sealed class EncodeStallMonitor(
    DateTimeOffset startedAt,
    TimeSpan? silenceLimit = null,
    TimeSpan? exitGraceAfterFinalReport = null)
{
    /// <summary>
    /// Longer than any single frame plausibly takes even on the slowest software preset at 4K on
    /// a small CPU, and longer than a hardware encoder's initialisation. Below this the cost of a
    /// false positive (a killed encode that would have finished) outweighs the cost of waiting.
    /// </summary>
    public static readonly TimeSpan DefaultSilenceLimit = TimeSpan.FromMinutes(30);

    /// <summary>
    /// After <c>progress=end</c> the trailer is written; what remains is encoder teardown. Two
    /// minutes is orders of magnitude more than that takes and still short enough that a stuck
    /// job is reported the same hour, not the next morning.
    /// </summary>
    public static readonly TimeSpan DefaultExitGraceAfterFinalReport = TimeSpan.FromMinutes(2);

    private readonly TimeSpan _silenceLimit = silenceLimit ?? DefaultSilenceLimit;
    private readonly TimeSpan _exitGrace = exitGraceAfterFinalReport ?? DefaultExitGraceAfterFinalReport;
    private DateTimeOffset _lastActivityAt = startedAt;
    private DateTimeOffset? _finalReportAt;

    public DateTimeOffset? FinalReportAt => _finalReportAt;

    /// <summary>Any line on the progress pipe is proof of life, whether or not it parsed.</summary>
    public void Touch(DateTimeOffset now) => _lastActivityAt = now;

    /// <summary>The <c>progress=end</c> block was read; the clock for exiting starts here.</summary>
    public void FinalReported(DateTimeOffset now)
    {
        _lastActivityAt = now;
        _finalReportAt ??= now;
    }

    /// <summary>
    /// Null while the encode is healthy or paused. Pausing suspends FFmpeg with SIGSTOP, which
    /// silences it deliberately, so silence during a pause is not evidence of anything; the
    /// activity clock is moved up to the moment the pause was observed so a resumed encode gets
    /// the full window again.
    /// </summary>
    public EncodeStallKind? Check(DateTimeOffset now, bool paused = false)
    {
        if (paused)
        {
            _lastActivityAt = now;
            return null;
        }

        if (_finalReportAt is { } finalReportAt)
        {
            return now - finalReportAt >= _exitGrace ? EncodeStallKind.DidNotExitAfterFinalReport : null;
        }

        return now - _lastActivityAt >= _silenceLimit ? EncodeStallKind.NoProgressReported : null;
    }

    /// <summary>The operator-facing reason for a kill, naming what was observed and for how long.</summary>
    public string Describe(EncodeStallKind kind) => kind switch
    {
        EncodeStallKind.DidNotExitAfterFinalReport =>
            $"ffmpeg reported the encode complete but had not exited {Minutes(_exitGrace)} later, so it was "
            + "terminated. The output was discarded; retry the job, and if this repeats for one encoder "
            + "(SVT-AV1 in particular) please report it with the ffmpeg command shown in the job details.",
        EncodeStallKind.NoProgressReported =>
            $"ffmpeg reported no progress for {Minutes(_silenceLimit)} while the queue was not paused, so "
            + "it was treated as hung and terminated. The output was discarded; retry the job.",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown stall kind.")
    };

    private static string Minutes(TimeSpan span) =>
        span.TotalMinutes == 1 ? "1 minute" : $"{span.TotalMinutes:0.#} minutes";
}
