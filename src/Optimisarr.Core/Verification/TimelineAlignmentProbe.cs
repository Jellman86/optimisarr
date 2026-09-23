using System.Globalization;
using System.Text.Json;

namespace Optimisarr.Core.Verification;

/// <summary>
/// Finds how far the candidate's pictures sit from the reference's, by trying.
///
/// <para>The shift this replaces was derived from the containers' own account of themselves — the
/// distorted's lead less the reference's. That number is zero for almost every real pair, so the
/// correction was seldom applied; and where it mattered it could not have been right anyway. Two
/// episodes of the same show, encoded by the same command on the same machine, are identical in
/// every header field and need opposite answers:</para>
///
/// <code>
/// S13E20   shift 0 → harmonic  0.21      shift +1 frame → harmonic 92.32
/// S13E21   shift 0 → harmonic 89.88      shift +1 frame → harmonic  0.31
/// </code>
///
/// <para>The difference is not in the headers. It is that frames are still occasionally lost in an
/// encode — one in the first of those files, six in the second — so whether a window lines up
/// depends on how many went missing before it. No arithmetic over metadata can know that.</para>
///
/// <para>Both sidecars measure this the same way, to the same offsets. A server that guessed while
/// its workers measured would disagree with them about the same pair of files.</para>
/// </summary>
public static class TimelineAlignmentProbe
{
    /// <summary>Offsets to try, in frames. Further out than one frame is a different file.</summary>
    public static IReadOnlyList<int> FramesToTry { get; } = [0, 1, -1];

    public const double ProbeStartSeconds = 60;
    public const double ProbeLeadSeconds = 1;
    public const double ProbeSeconds = 2;

    /// <summary>The arguments for one candidate offset's probe.</summary>
    public static IReadOnlyList<string> Arguments(
        string reference, string distorted, double shiftSeconds, string logPath,
        double probeStartSeconds = ProbeStartSeconds)
    {
        var lead = ProbeLeadSeconds.ToString("G", CultureInfo.InvariantCulture);
        var length = ProbeSeconds.ToString("G", CultureInfo.InvariantCulture);
        var start = probeStartSeconds.ToString("G", CultureInfo.InvariantCulture);
        var offset = (shiftSeconds * 1_000_000).ToString("F6", CultureInfo.InvariantCulture);
        var graph =
            $"[0:v]settb=AVTB,setpts=PTS-{offset},trim=start={lead}:duration={length},"
            + "settb=AVTB,setpts=PTS-STARTPTS,scale=320:240:flags=bilinear,format=yuv420p[dist];"
            + $"[1:v]settb=AVTB,trim=start={lead}:duration={length},"
            + "settb=AVTB,setpts=PTS-STARTPTS,scale=320:240:flags=bilinear,format=yuv420p[ref];"
            + "[dist][ref]libvmaf=n_threads=4:n_subsample=1:"
            + $"log_fmt=json:log_path={FfmpegFilterOptionPath.Escape(logPath)}:shortest=1:repeatlast=0";

        return
        [
            "-nostdin", "-v", "error",
            "-ss", start, "-threads", "4", "-i", distorted,
            "-ss", start, "-threads", "4", "-i", reference,
            "-lavfi", graph,
            "-t", length, "-f", "null", "-",
        ];
    }

    /// <summary>
    /// The mean of a probe's frame scores. The mean rather than the harmonic mean on purpose: this
    /// chooses between alignments rather than judging quality, and a harmonic mean collapses to
    /// near zero for every offset once any of them holds a zero.
    /// </summary>
    public static double? MeanScore(string json)
    {
        List<double> scores = [];
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("frames", out var frames)
                || frames.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var frame in frames.EnumerateArray())
            {
                if (frame.TryGetProperty("metrics", out var metrics)
                    && metrics.TryGetProperty("vmaf", out var vmaf)
                    && vmaf.TryGetDouble(out var score))
                {
                    scores.Add(score);
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return scores.Count == 0 ? null : scores.Average();
    }

    /// <summary>The chosen offset written the way the command builder writes seconds.</summary>
    public static string Format(double shiftSeconds) =>
        Math.Abs(shiftSeconds) < 0.0000005
            ? "0"
            : shiftSeconds.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>How long one picture lasts at this rate, or null when the rate is unusable.</summary>
    public static double? FrameSeconds(double? framesPerSecond) =>
        framesPerSecond is > 0 ? 1 / framesPerSecond.Value : null;

}
