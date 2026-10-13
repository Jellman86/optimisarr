using System.Globalization;
using Optimisarr.Core.Verification;

namespace Optimisarr.Sidecar.Core.Session;

/// <summary>
/// Chooses one bounded frame offset using the real measurement graph. Evidence spans each
/// sampled window so a static opening cannot hide a later mismatch. Conflicting scenes keep
/// the unshifted comparison rather than concealing lost or repeated pictures (#406).
/// </summary>
public static class TimelineAlignment
{
    /// <summary>Offsets to try, in frames. A candidate further out than one frame either way is
    /// not misaligned, it is a different file.</summary>
    public static IReadOnlyList<int> FramesToTry => TimelineAlignmentProbe.FramesToTry;

    /// <summary>
    /// Length of each evidence block, and the fallback bound for commands without a window.
    /// </summary>
    public const double ProbeSeconds = TimelineAlignmentProbe.ProbeSeconds;

    /// <summary>
    /// How far another offset must beat the unshifted timeline to be chosen. A frame of real
    /// misalignment costs tens of points; anything closer is the scene, not the timeline.
    /// </summary>
    public const double ChoiceMarginPoints = TimelineAlignmentProbe.ChoiceMarginPoints;

    /// <summary>
    /// The shift to hand the server's measurement command, written the way it writes seconds, or
    /// null when no offset could be scored at all.
    ///
    /// <para>Each offset is tried with that command itself across its bounded window. An
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
        frameSeconds = TimelineAlignmentProbe.CommandFrameSeconds(command) ?? frameSeconds;
        var scored = new List<(int Frames, IReadOnlyList<AlignmentScore> Scores)>();
        foreach (var frames in FramesToTry)
        {
            var log = Path.Combine(scratch, $"align-{frames}.json");
            try
            {
                var probe = TimelineAlignmentProbe.Prepare(
                    MeasurementPlaceholders.Resolve(command, candidate, source, log, Shift(frames, frameSeconds)));
                var run = await transcoder.RunAsync(ffmpegPath, probe, null, cancellationToken);
                if (!run.Succeeded || !File.Exists(log))
                {
                    continue;
                }

                if (TimelineAlignmentProbe.ReadScores(await File.ReadAllTextAsync(log, cancellationToken)) is { } scores)
                {
                    scored.Add((frames, scores));
                }
            }
            finally
            {
                try { File.Delete(log); } catch (IOException) { /* it was scratch */ }
            }
        }

        return TimelineAlignmentProbe.ChooseWindow(scored, 1 / frameSeconds,
            TimelineAlignmentProbe.Duration(command)) is { } chosen ? Shift(chosen, frameSeconds) : null;
    }

    /// <summary>
    /// The measurement stopped after <paramref name="seconds"/> of output: its own limit replaced,
    /// or one added before the output when it has none.
    /// </summary>
    public static IReadOnlyList<string> Truncate(IReadOnlyList<string> measurement, double seconds)
        => TimelineAlignmentProbe.Truncate(measurement, seconds);

    private static string Shift(int frames, double frameSeconds) => TimelineLead.Shift(0, -frames * frameSeconds);

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
