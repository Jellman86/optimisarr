namespace Optimisarr.Core.Queue;

/// <summary>
/// How far apart a library's video re-encodes place keyframes. A library stores seconds, so the
/// setting means the same at 24 and 60 fps; each encode receives it as a frame count (<c>-g</c>),
/// which every supported encoder family reads as its GOP length.
///
/// <para>Only a ceiling is set, and the encoder keeps its own minimum. Most encoders still add
/// keyframes at scene cuts; VideoToolbox places them only at the interval.</para>
/// </summary>
public static class KeyframeInterval
{
    /// <summary>
    /// Under a second, a keyframe costs more than seeking gains: the file grows noticeably and no
    /// player needs it, which works against the only reason to re-encode.
    /// </summary>
    public const int MinimumSeconds = 1;

    /// <summary>
    /// Past twenty seconds a seek or a resumed stream waits on many seconds of decoding, and
    /// servers that cut segments at keyframes produce segments far longer than they ask for.
    /// </summary>
    public const int MaximumSeconds = 20;

    /// <summary>The fastest rate a probe reports (see MediaProbeService).</summary>
    private const int MaximumFrameRate = 1_000;

    /// <summary>
    /// The largest GOP length the server sends: the longest interval at the fastest probed rate.
    /// Workers refuse anything larger, and it fits every encoder's own field (QSV's is 16-bit).
    /// </summary>
    public const int MaximumFrames = MaximumSeconds * MaximumFrameRate;

    /// <summary>
    /// The GOP length a VideoToolbox encode receives when the library leaves the interval to the
    /// encoder. FFmpeg gives VideoToolbox only its generic default of 12 frames, a keyframe about
    /// twice a second, which made Mac output far larger at the same quality (#612). 250 is the
    /// default x264 and x265 use and FFmpeg gives NVENC and AMF.
    /// </summary>
    public const int VideoToolboxDefaultFrames = 250;

    public static bool IsValidSeconds(int seconds) => seconds is >= MinimumSeconds and <= MaximumSeconds;

    /// <summary>
    /// The GOP length for <paramref name="seconds"/> at <paramref name="frameRate"/>, the rate the
    /// encode produces. Null for the encoder default, and when the rate is unknown: the encoder's
    /// own interval is the one every earlier encode used, which is safer than one counted from a
    /// guessed rate. A variable-rate source is counted at its average rate, so its keyframes land
    /// about that many seconds apart.
    /// </summary>
    public static int? Frames(int? seconds, double? frameRate)
    {
        if (seconds is not { } interval || !IsValidSeconds(interval)
            || frameRate is not { } rate || !double.IsFinite(rate) || rate <= 0)
        {
            return null;
        }

        var frames = Math.Round(interval * Math.Min(rate, MaximumFrameRate), MidpointRounding.AwayFromZero);
        return (int)Math.Max(1, frames);
    }
}
