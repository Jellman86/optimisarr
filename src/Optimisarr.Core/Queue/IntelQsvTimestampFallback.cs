namespace Optimisarr.Core.Queue;

/// <summary>
/// Recognises an Intel QSV encode that stopped because the source's picture times jump.
///
/// <para>QSV works out each packet's decode time from a fixed frame interval. When the source's
/// presentation times jump, as they do across a 4-second gap at 17:08 in two Pokémon WEBDL
/// episodes, it emits a decode time earlier than the one before. The muxer refuses that packet
/// and FFmpeg stops, on the server and on a worker alike, at the same frame every time.
/// h264_qsv fails the same way; B-frames off avoids it at about twice the size; VA-API on the same
/// GPU and the software encoders keep every frame. So the job is retried with another encoder
/// rather than repeated, and the source's timing is never altered.</para>
/// </summary>
public static class IntelQsvTimestampFallback
{
    /// <summary>The attempt-history reason for an attempt stopped this way.</summary>
    public const string AttemptReason = "IntelQsvTimestamps";

    /// <summary>The job's retry reason while it runs again without QSV.</summary>
    public const string RetryReason = "AvoidIntelQsv";

    /// <summary>
    /// True when the QSV encoder's own output stream was refused by the muxer for a backward
    /// decode time. Both halves must sit on one line: FFmpeg also logs a harmless
    /// "Non-monotonic DTS ... changing to" warning for copied audio, and a run that then fails for
    /// another reason, such as a full disk, also mentions the muxer.
    /// </summary>
    public static bool Applies(string? encoder, string? ffmpegLog) =>
        encoder is not null
        && encoder.EndsWith("_qsv", StringComparison.OrdinalIgnoreCase)
        && ffmpegLog is not null
        && ffmpegLog.Split('\n').Any(line =>
            line.Contains("_qsv", StringComparison.OrdinalIgnoreCase)
            && line.Contains("Non-monotonic DTS", StringComparison.OrdinalIgnoreCase)
            && line.Contains("Error submitting a packet to the muxer", StringComparison.OrdinalIgnoreCase));
}
