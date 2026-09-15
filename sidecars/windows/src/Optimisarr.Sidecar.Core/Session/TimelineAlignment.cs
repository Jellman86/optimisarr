using System.Globalization;
using System.Text.Json;

namespace Optimisarr.Sidecar.Core.Session;

/// <summary>
/// Finds how far the candidate's pictures sit from the source's, by trying.
///
/// <para>The <c>distortedShift</c> the server leaves a token for was derived from the container's
/// own account of itself — the video stream's start less the container's. That number is zero for
/// every file either sidecar has ever measured, because the two starts are equal in every real
/// container, so the correction has never once been applied.</para>
///
/// <para>It could not have worked anyway. Two episodes of the same show, encoded by the same
/// command on the same machine, are identical in every header field — container start, stream
/// start, first decoded frame — and yet one needs its candidate moved by a frame and the other is
/// destroyed by the same move:</para>
///
/// <code>
/// S13E20   shift 0 → harmonic  0.21      shift +1 frame → harmonic 92.32
/// S13E21   shift 0 → harmonic 89.88      shift +1 frame → harmonic  0.31
/// </code>
///
/// <para>The difference is not in the headers. It is that frames are still occasionally lost in
/// the encode — one in the first of those files, six in the second — so whether a given window
/// lines up depends on how many went missing before it. No arithmetic over metadata can know
/// that.</para>
///
/// <para>So this measures it instead. The worker is the only machine holding both files, a couple
/// of seconds of pictures is enough to tell a frame's misalignment from a good match, and the
/// answer is the one the real measurement then uses. The same probe as the macOS sidecar's, to the
/// same offsets, written the same way — two workers that aligned differently would be reporting
/// measurements of different things.</para>
/// </summary>
public static class TimelineAlignment
{
    /// <summary>Offsets to try, in frames. A candidate further out than one frame either way is
    /// not misaligned, it is a different file.</summary>
    public static IReadOnlyList<int> FramesToTry { get; } = [0, 1, -1];

    /// <summary>Far enough in to be past titles and black, short enough that three cost a second.</summary>
    public const double ProbeStartSeconds = 60;
    public const double ProbeLeadSeconds = 1;
    public const double ProbeSeconds = 2;

    /// <summary>
    /// The shift to hand the server's measurement commands, written the way it writes seconds, or
    /// null when no probe could be scored at all.
    /// </summary>
    public static async Task<string?> MeasureAsync(
        ITranscoder transcoder,
        string ffmpegPath,
        string source,
        string candidate,
        double frameSeconds,
        string scratch,
        CancellationToken cancellationToken)
    {
        double? bestShift = null;
        var bestScore = double.NegativeInfinity;

        foreach (var frames in FramesToTry)
        {
            var shift = frames * frameSeconds;
            var log = Path.Combine(scratch, $"align-{frames}.json");
            try
            {
                var run = await transcoder.RunAsync(
                    ffmpegPath, Arguments(source, candidate, shift, log), null, cancellationToken);
                if (!run.Succeeded || !File.Exists(log))
                {
                    continue;
                }

                if (MeanScore(await File.ReadAllTextAsync(log, cancellationToken)) is { } score
                    && score > bestScore)
                {
                    bestScore = score;
                    bestShift = shift;
                }
            }
            finally
            {
                try { File.Delete(log); } catch (IOException) { /* it was scratch */ }
            }
        }

        return bestShift is { } chosen ? TimelineLead.Shift(0, -chosen) : null;
    }

    /// <summary>
    /// The probe: the same pairing the real measurement does, on a short window at a small size.
    /// Same shape deliberately — an alignment chosen by a differently-built comparison would be
    /// the alignment for a measurement nobody runs.
    /// </summary>
    public static IReadOnlyList<string> Arguments(
        string source, string candidate, double shift, string log)
    {
        var lead = ProbeLeadSeconds.ToString("G", CultureInfo.InvariantCulture);
        var length = ProbeSeconds.ToString("G", CultureInfo.InvariantCulture);
        var offset = (shift * 1_000_000).ToString("F6", CultureInfo.InvariantCulture);
        var graph =
            $"[0:v]settb=AVTB,setpts=PTS-{offset},trim=start={lead}:duration={length},"
            + "settb=AVTB,setpts=PTS-STARTPTS,scale=320:240:flags=bilinear,format=yuv420p[dist];"
            + $"[1:v]settb=AVTB,trim=start={lead}:duration={length},"
            + "settb=AVTB,setpts=PTS-STARTPTS,scale=320:240:flags=bilinear,format=yuv420p[ref];"
            + "[dist][ref]libvmaf=model=version=vmaf_v0.6.1:n_threads=4:n_subsample=1:"
            + $"log_fmt=json:log_path={FilterPath.ForFilterOption(log)}:shortest=1:repeatlast=0";

        return
        [
            "-nostdin", "-v", "error",
            "-ss", ProbeStartSeconds.ToString("G", CultureInfo.InvariantCulture), "-i", candidate,
            "-ss", ProbeStartSeconds.ToString("G", CultureInfo.InvariantCulture), "-i", source,
            "-lavfi", graph,
            "-t", length, "-f", "null", "-",
        ];
    }

    /// <summary>
    /// The mean of a probe's frame scores. The mean rather than the harmonic mean on purpose: this
    /// is choosing between alignments, not judging quality, and the mean separates them cleanly
    /// while staying readable when every alignment is poor.
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

    /// <summary>
    /// How long one picture lasts, from the source's own declared rate, or null when it cannot be
    /// read — which leaves the caller its own default rather than a guess dressed as a measurement.
    /// </summary>
    public static double? FrameSeconds(string probeOutput)
    {
        var parts = probeOutput.Trim().Split('/');
        return parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator)
            && numerator > 0 && denominator > 0
                ? denominator / numerator
                : null;
    }

    /// <summary>What to ask ffprobe for, to learn how long a picture lasts.</summary>
    public static IReadOnlyList<string> FrameRateArguments(string file) =>
    [
        "-v", "error", "-select_streams", "v:0",
        "-show_entries", "stream=r_frame_rate", "-of", "csv=p=0", file,
    ];
}
