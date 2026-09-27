using System.Globalization;
using System.Text;
using Optimisarr.Core.Calibration;
using Optimisarr.Core.Verification;

internal sealed record StudyOptions(
    string Ffmpeg, string Ffprobe, string Output, IReadOnlyList<string> Sources, IReadOnlyList<int> Qualities,
    string Codec, string Encoder, string Preset, string? CandidateHd, string? CandidateUhd, string? ReportFrom,
    string MeasurementFfmpeg)
{
    public const string BaselineHd = "vmaf_v0.6.1";
    public const string BaselineUhd = "vmaf_4k_v0.6.1";
    public const string DefaultCandidateHd = "vmaf_v1.0.16_3d0h";
    public const string DefaultCandidateUhd = "vmaf_v1.0.16_1d5h_2160";

    public const string Usage = """
        Usage: Optimisarr.VmafStudy --ffmpeg PATH --ffprobe PATH --out DIR [options] SOURCE...
          --ffmpeg PATH        encoding FFmpeg
          --measurement-ffmpeg PATH  scoring FFmpeg with the candidate models (defaults to --ffmpeg)
          --ffprobe PATH       ffprobe
          --out DIR            where clips, scores.csv and report.md are written
          --qualities LIST     CRF ladder, default 18,22,26,30,34
          --encoder NAME       default libx265 (codec hevc)
          --preset NAME        default medium
          --model-hd NAME      candidate model for HD, default vmaf_v1.0.16_3d0h
          --model-uhd NAME     candidate model for UHD, default vmaf_v1.0.16_1d5h_2160
          --report-from CSV    rebuild report.md from an earlier scores.csv; no sources needed
        """;

    public string CandidateModelFor(int width, int height) =>
        width >= 3840 || height >= 2160 ? CandidateUhd ?? DefaultCandidateUhd : CandidateHd ?? DefaultCandidateHd;

    public static StudyOptions? Parse(string[] args)
    {
        try { return ParseChecked(args); }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException) { return null; }
    }

    private static StudyOptions? ParseChecked(string[] args)
    {
        string? ffmpeg = null, ffprobe = null, output = null, hd = null, uhd = null, reportFrom = null, measurement = null;
        string encoder = "libx265", preset = "medium";
        IReadOnlyList<int> qualities = [18, 22, 26, 30, 34];
        var sources = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--ffmpeg": ffmpeg = Next(); break;
                case "--measurement-ffmpeg": measurement = Next(); break;
                case "--ffprobe": ffprobe = Next(); break;
                case "--out": output = Next(); break;
                case "--qualities": qualities = Next().Split(',').Select(q => int.Parse(q, CultureInfo.InvariantCulture)).ToList(); break;
                case "--encoder": encoder = Next(); break;
                case "--preset": preset = Next(); break;
                case "--model-hd": hd = Next(); break;
                case "--model-uhd": uhd = Next(); break;
                case "--report-from": reportFrom = Next(); break;
                default:
                    if (args[i].StartsWith('-')) return null;
                    sources.Add(args[i]);
                    break;
            }
        }
        var codec = encoder.Contains("264", StringComparison.Ordinal) ? "h264"
            : encoder.Contains("av1", StringComparison.Ordinal) ? "av1" : "hevc";
        if (qualities.Count == 0 || qualities.Distinct().Count() != qualities.Count || qualities.Any(q => q is < 0 or > 63)
            || string.IsNullOrWhiteSpace(output) || string.IsNullOrWhiteSpace(encoder) || string.IsNullOrWhiteSpace(preset)) return null;
        if (reportFrom is not null && output is not null)
        {
            return new StudyOptions(ffmpeg ?? "", ffprobe ?? "", output, sources, qualities, codec, encoder, preset, hd, uhd, reportFrom, measurement ?? ffmpeg ?? "");
        }
        return ffmpeg is null || ffprobe is null || output is null || sources.Count == 0
            ? null
            : new StudyOptions(ffmpeg, ffprobe, output, sources.Distinct().ToList(), qualities, codec, encoder, preset, hd, uhd, null, measurement ?? ffmpeg);
    }
}
