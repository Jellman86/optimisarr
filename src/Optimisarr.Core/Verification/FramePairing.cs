using System.Globalization;

namespace Optimisarr.Core.Verification;

/// <summary>
/// Decides whether a candidate's frames can be compared with its source's by number rather than by
/// timestamp.
///
/// <para>An encode that keeps every frame can still stamp stretches of them a frame early: one
/// candidate held all 34,046 of its source's frames, yet for tens of seconds at a time each carried
/// the previous frame's time. Pairing on timestamps then compared each picture with its neighbour
/// and scored a clean encode at a harmonic mean of 16; pairing by number scored the same windows at
/// 92–94 (#269). When the counts differ a frame was lost or added, every later number is off by
/// it, and the timestamps are the better guide, so pairing by number applies only to equal counts.</para>
///
/// <para>Equal counts permit sequential comparison but do not prove picture identity or timing.
/// Structure, decoded-picture retention, timing, quality and replacement checks remain independent.
/// Generated acceptance fixtures also prove ordered picture identities.</para>
/// </summary>
public static class FramePairing
{
    public static bool Applies(int? sourceFrames, int? candidateFrames) =>
        sourceFrames is > 0 && sourceFrames == candidateFrames;

    /// <summary>What to ask ffprobe for, to count a file's pictures by decoding frames; packet counts can include non-picture packets.</summary>
    public static IReadOnlyList<string> CountArguments(string file) =>
    [
        "-v", "error", "-select_streams", TimestampIntegrityCheck.MovingPictureStreamSpecifier, "-count_frames",
        "-show_entries", "stream=nb_read_frames", "-of", "csv=p=0", file,
    ];

    /// <summary>
    /// Whether every frame window in a measurement command holds pictures and ends within a file of this many.
    /// The server places frame windows before anyone has counted the source, at the rate the file
    /// reports, and a variable-frame-rate file holds fewer pictures than that rate implies. A
    /// window past the last frame decodes nothing, writes no score and still exits cleanly, so a
    /// worker checks this against its own count and measures such a window by timestamp instead.
    /// A command with no frame window, such as a complete-file comparison, always fits.
    /// </summary>
    public static bool FitsWithin(IReadOnlyList<string> command, int frames) =>
        command.SelectMany(argument => FrameWindow.Matches(argument))
            .All(window => long.TryParse(window.Groups["start"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var start)
                && long.TryParse(window.Groups["end"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var end)
                && start < end && end <= frames);

    private static readonly System.Text.RegularExpressions.Regex FrameWindow =
        new(@"trim=start_frame=(?<start>\d+):end_frame=(?<end>\d+)", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public static int? ParseCount(string output) =>
        int.TryParse(output.Trim().TrimEnd(','), NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            ? count
            : null;
}
