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

    public static bool Applies(string? encoder, string? ffmpegLog) =>
        encoder is not null
        && encoder.EndsWith("_qsv", StringComparison.OrdinalIgnoreCase)
        && ffmpegLog is not null
        && ffmpegLog.Contains("Non-monotonic DTS", StringComparison.OrdinalIgnoreCase)
        && ffmpegLog.Contains("muxer", StringComparison.OrdinalIgnoreCase);
}
