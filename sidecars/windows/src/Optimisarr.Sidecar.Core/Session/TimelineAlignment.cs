using System.Globalization;
using System.Text.Json;
using Optimisarr.Core.Verification;

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

    /// <summary>
    /// Seconds of the window each offset is tried on: long enough to hold motion or a cut, which
    /// is what separates a frame of misalignment from a good match. Two seconds of a static shot
    /// scored every offset alike.
    /// </summary>
    public const double ProbeSeconds = 5;

    /// <summary>
    /// How far another offset must beat the unshifted timeline to be chosen. A frame of real
    /// misalignment costs tens of points; anything closer is the scene, not the timeline.
    /// </summary>
    public const double ChoiceMarginPoints = 2;

    /// <summary>
    /// The shift to hand the server's measurement command, written the way it writes seconds, or
    /// null when no offset could be scored at all.
    ///
    /// <para>Each offset is tried with that command itself, cut to <see cref="ProbeSeconds"/>. An
    /// earlier probe built its own graph — no cadence grid, no lead correction, a different seek —
    /// and the offset it liked was a frame wrong for the measurement that followed, failing clean
    /// encodes at harmonic 9.</para>
    /// </summary>
    public static async Task<string?> MeasureAsync(
        ITranscoder transcoder,
        string ffmpegPath,
        IReadOnlyList<string> command,
        string source,
        string candidate,
        double frameSeconds,
        string scratch,
        CancellationToken cancellationToken)
    {
        var scored = new List<(int Frames, double Mean)>(FramesToTry.Count);
        foreach (var frames in FramesToTry)
        {
            var log = Path.Combine(scratch, $"align-{frames}.json");
            try
            {
                var probe = Truncate(
                    MeasurementPlaceholders.Resolve(command, candidate, source, log, Shift(frames, frameSeconds)),
                    ProbeSeconds);
                var run = await transcoder.RunAsync(ffmpegPath, probe, null, cancellationToken);
                if (!run.Succeeded || !File.Exists(log))
                {
                    continue;
                }

                if (MeanScore(await File.ReadAllTextAsync(log, cancellationToken)) is { } mean)
                {
                    scored.Add((frames, mean));
                }
            }
            finally
            {
                try { File.Delete(log); } catch (IOException) { /* it was scratch */ }
            }
        }

        return Choose(scored) is { } chosen ? Shift(chosen, frameSeconds) : null;
    }

    /// <summary>
    /// The measurement stopped after <paramref name="seconds"/> of output: its own limit replaced,
    /// or one added before the output when it has none.
    /// </summary>
    public static IReadOnlyList<string> Truncate(IReadOnlyList<string> measurement, double seconds)
    {
        var length = seconds.ToString("G", CultureInfo.InvariantCulture);
        var arguments = measurement.ToList();
        var limit = arguments.LastIndexOf("-t");
        if (limit >= 0 && limit + 1 < arguments.Count)
        {
            arguments[limit + 1] = length;
            return arguments;
        }

        var output = arguments.LastIndexOf("-f");
        arguments.InsertRange(output >= 0 ? output : arguments.Count, ["-t", length]);
        return arguments;
    }

    /// <summary>
    /// The offset, in frames, whose probe matched best, or null when none could be scored. The
    /// unshifted timeline keeps the window unless another offset beats it clearly.
    /// </summary>
    public static int? Choose(IReadOnlyList<(int Frames, double Mean)> scored)
    {
        if (scored.Count == 0)
        {
            return null;
        }

        var best = scored.MaxBy(entry => entry.Mean);
        return best.Frames != 0
            && scored.Any(entry => entry.Frames == 0 && best.Mean - entry.Mean < ChoiceMarginPoints)
                ? 0
                : best.Frames;
    }

    private static string Shift(int frames, double frameSeconds) => TimelineLead.Shift(0, -frames * frameSeconds);

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
        "-v", "error", "-select_streams", TimestampIntegrityCheck.MovingPictureStreamSpecifier,
        "-show_entries", "stream=r_frame_rate", "-of", "csv=p=0", file,
    ];
}
