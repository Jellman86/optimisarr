using System.Text.RegularExpressions;

namespace Optimisarr.Core.Activity;

/// <summary>The title (and optional year) parsed from a media file's path, for an artwork lookup.</summary>
public sealed record MediaTitle(string Title, int? Year);

/// <summary>
/// Extracts a searchable title and year from a library-relative media path so artwork can be
/// looked up on a connected media server. It is a best-effort heuristic — the server's own search
/// is fuzzy, so a roughly-cleaned title is enough — and pure, so it is unit tested. For TV it uses
/// the show folder (the segment above a "Season N" folder); for film the title folder or filename.
/// </summary>
public static partial class MediaTitleParser
{
    public static MediaTitle? Parse(string? relativePath, bool isTv)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        var segments = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return null;
        }

        var fileName = StripExtension(segments[^1]);

        // Pick the best candidate string to clean: for TV, the show folder above "Season N";
        // for film, the immediate parent folder (TRaSH-style "Title (Year)") or the filename.
        string candidate;
        if (isTv)
        {
            var seasonIndex = Array.FindIndex(segments, s => SeasonFolder().IsMatch(s));
            candidate = seasonIndex > 0 ? segments[seasonIndex - 1] : segments[0];
        }
        else
        {
            candidate = segments.Length >= 2 ? segments[^2] : fileName;
        }

        var year = ExtractYear(candidate) ?? ExtractYear(fileName);
        var title = Clean(candidate);
        // If the folder cleaned down to nothing useful, fall back to the filename.
        if (title.Length == 0)
        {
            title = Clean(fileName);
        }

        return title.Length == 0 ? null : new MediaTitle(title, year);
    }

    private static string StripExtension(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }

    private static int? ExtractYear(string text)
    {
        var match = YearToken().Match(text);
        return match.Success && int.TryParse(match.Groups[1].Value, out var year) ? year : null;
    }

    private static string Clean(string text)
    {
        // Cut everything from the first release marker (resolution, source, codec, SxxExx, year-in-parens).
        var cut = ReleaseMarker().Match(text);
        var trimmed = cut.Success ? text[..cut.Index] : text;
        // Separators used in scene names become spaces; collapse and trim punctuation.
        trimmed = trimmed.Replace('.', ' ').Replace('_', ' ');
        trimmed = WhitespaceRun().Replace(trimmed, " ").Trim(' ', '-', '–');
        return trimmed;
    }

    [GeneratedRegex(@"^season\s*\d+$|^s\d+$|^specials$", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonFolder();

    // A bare or parenthesised 4-digit year (1900–2099).
    [GeneratedRegex(@"\(?((?:19|20)\d{2})\)?")]
    private static partial Regex YearToken();

    // First token that marks the start of release metadata rather than the title.
    [GeneratedRegex(
        @"(\(?(?:19|20)\d{2}\)?|\bS\d{1,2}E\d{1,2}\b|\b\d{3,4}p\b|\b(?:bluray|blu-ray|remux|web-?rip|web-?dl|hdtv|dvdrip|bdrip|x264|x265|h264|h265|hevc|avc|aac|dts|ddp?5)\b)",
        RegexOptions.IgnoreCase)]
    private static partial Regex ReleaseMarker();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex WhitespaceRun();
}
