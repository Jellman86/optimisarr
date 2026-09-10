using System.Text.RegularExpressions;

namespace Optimisarr.Core.Queue;

/// <summary>A rectangle of picture to keep, in the source's own pixel coordinates.</summary>
public sealed record CropRect(int Width, int Height, int X, int Y)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;

    /// <summary>The compact form persisted on a job, and parsed back for a retry.</summary>
    public override string ToString() => $"{Width}:{Height}:{X}:{Y}";

    public static CropRect? TryParse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var parts = value.Split(':');
        return parts.Length == 4
            && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h)
            && int.TryParse(parts[2], out var x) && int.TryParse(parts[3], out var y)
            && w > 0 && h > 0 && x >= 0 && y >= 0
                ? new CropRect(w, h, x, y)
                : null;
    }
}

/// <summary>Reads what FFmpeg's <c>cropdetect</c> filter reports on stderr.</summary>
public static partial class CropDetectParser
{
    // One report per analysed frame, e.g.
    //   [Parsed_cropdetect_0 @ 0x7f8] x1:0 x2:1919 y1:138 y2:941 w:1920 h:800 x:0 y:140 ... crop=1920:800:0:140
    // Only the trailing crop= expression is needed; it is the filter's own rounded answer.
    [GeneratedRegex(@"cropdetect.*\bcrop=(\d+):(\d+):(\d+):(\d+)\b")]
    private static partial Regex Report();

    public static CropRect? ParseLine(string line)
    {
        var match = Report().Match(line);
        return match.Success
            ? new CropRect(
                int.Parse(match.Groups[1].Value),
                int.Parse(match.Groups[2].Value),
                int.Parse(match.Groups[3].Value),
                int.Parse(match.Groups[4].Value))
            : null;
    }

    public static IReadOnlyList<CropRect> ParseAll(string stderr) =>
        stderr.Split('\n')
            .Select(ParseLine)
            .Where(rect => rect is not null)
            .Select(rect => rect!)
            .ToList();
}

/// <summary>
/// Turns several cropdetect reports into one crop, or into none.
///
/// This is where the safety of black-bar removal lives, because the verification gates cannot
/// provide it. VMAF compares the output against a reference cropped the same way, so a crop that
/// removes picture scores exactly as well as one that removes bars. Every rule here therefore
/// errs toward keeping picture: the union of everything any sampled window kept, so a dark scene
/// cannot narrow the answer for the bright ones; nothing at all for a change too small to be
/// letterboxing; nothing at all for a change too large to be real; and odd edges widened outward,
/// keeping a sliver of bar rather than shaving a line of picture.
/// </summary>
public static class CropPlanner
{
    /// <summary>Fewer removed pixels than this on both axes is encoder noise, not a bar.</summary>
    private const int MinimumRemovedPixels = 16;

    /// <summary>A crop retaining less than this fraction of an axis is a detection failure.</summary>
    private const double MinimumRetainedFraction = 0.5;

    public static CropRect? Plan(IReadOnlyList<CropRect> samples, PictureSize source)
    {
        if (samples.Count == 0)
        {
            return null;
        }

        // Keep everything any window kept.
        var left = samples.Min(s => s.X);
        var top = samples.Min(s => s.Y);
        var right = samples.Max(s => s.Right);
        var bottom = samples.Max(s => s.Bottom);

        // Widen to chroma-safe even edges, outward only, and never past the frame.
        left = Math.Max(0, left - left % 2);
        top = Math.Max(0, top - top % 2);
        right = Math.Min(source.Width, right + right % 2);
        bottom = Math.Min(source.Height, bottom + bottom % 2);

        var width = right - left;
        var height = bottom - top;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var removedWidth = source.Width - width;
        var removedHeight = source.Height - height;
        if (removedWidth < MinimumRemovedPixels && removedHeight < MinimumRemovedPixels)
        {
            return null;
        }

        if (width < source.Width * MinimumRetainedFraction
            || height < source.Height * MinimumRetainedFraction)
        {
            return null;
        }

        return new CropRect(width, height, left, top);
    }

    /// <summary>The <c>crop</c> filter for exactly this rectangle, all four numbers explicit.</summary>
    public static string Filter(CropRect crop) => $"crop={crop.Width}:{crop.Height}:{crop.X}:{crop.Y}";
}

/// <summary>
/// The detection pass: decode a short window with <c>cropdetect</c> attached and discard the
/// output. No encoder, so it is cheap enough to run over several windows.
/// </summary>
public static class CropDetectCommandBuilder
{
    // limit=24 is the filter's default black threshold, which tolerates the slightly-lifted
    // blacks of real transfers. round=2 keeps every reported edge on a chroma boundary. reset=0
    // accumulates over the whole window rather than restarting per frame, so a single dark frame
    // cannot narrow the report.
    private const string Filter = "cropdetect=limit=24:round=2:reset=0";

    /// <param name="durationSeconds">
    /// How much to analyse from <paramref name="startSeconds"/>; null reads to the end, which is
    /// what a short file's single whole-file window asks for.
    /// </param>
    public static IReadOnlyList<string> Build(string inputPath, int startSeconds, int? durationSeconds)
    {
        var args = new List<string> { "-nostdin", "-hide_banner", "-ss", startSeconds.ToString() };
        if (durationSeconds is > 0)
        {
            args.Add("-t");
            args.Add(durationSeconds.Value.ToString());
        }
        args.AddRange(["-i", inputPath, "-vf", Filter, "-an", "-sn", "-f", "null", "-"]);
        return args;
    }
}
