namespace Optimisarr.Core.Queue;

/// <summary>
/// Turns ffmpeg's raw stderr tail into a clear, actionable failure reason for the error classes
/// users actually hit, falling back to the raw text when unrecognised. Pure string mapping so it
/// is unit tested; the worker runs it before recording a job's failure message, so the Queue shows
/// "why and what to do" instead of a cryptic ffmpeg line.
/// </summary>
public static class FfmpegErrorInterpreter
{
    /// <summary>
    /// A friendly explanation for a known failure, or null if the error is not recognised (the
    /// caller then falls back to the raw stderr).
    /// </summary>
    public static string? Explain(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return null;
        }

        // Bitmap subtitles (Blu-ray PGS / DVD VobSub) cannot be converted to MP4's text-only
        // mov_text codec, and MP4 cannot carry them either.
        if (stderr.Contains("only possible from text to text or bitmap to bitmap", StringComparison.OrdinalIgnoreCase))
        {
            return "This file has image-based subtitles (e.g. Blu-ray PGS or DVD VobSub) that an MP4 "
                + "container can't store, so the conversion failed. Set this library's target container to "
                + "MKV to keep those subtitles, or remove them. The original was not touched.";
        }

        if (stderr.Contains("Error setting option preset", StringComparison.OrdinalIgnoreCase))
        {
            return "Invalid encoder effort: FFmpeg rejected the saved preset for the selected encoder. "
                + "Edit the library and choose Encoder default, Fast, Balanced, or Efficient. "
                + "The original was not touched.";
        }

        return null;
    }
}
