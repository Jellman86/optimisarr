namespace Optimisarr.Core.Library;

/// <summary>
/// Classifies subtitle streams as image-based (bitmap) or text. It matters for the output
/// container: MP4 can only carry text subtitles (as mov_text), so a source with bitmap subtitles
/// (Blu-ray PGS, DVD VobSub) must be muxed to MKV to keep them. Pure name matching, unit tested.
/// </summary>
public static class SubtitleClassifier
{
    // ffprobe codec_name values for bitmap subtitle formats. Everything else (subrip, ass, ssa,
    // mov_text, webvtt, …) is text and converts/copies fine.
    private static readonly HashSet<string> ImageBasedCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "hdmv_pgs_subtitle",
        "pgssub",
        "dvd_subtitle",
        "dvdsub",
        "dvb_subtitle",
        "dvbsub",
        "xsub",
    };

    public static bool IsImageBased(string? codecName) =>
        !string.IsNullOrWhiteSpace(codecName) && ImageBasedCodecs.Contains(codecName.Trim());
}
