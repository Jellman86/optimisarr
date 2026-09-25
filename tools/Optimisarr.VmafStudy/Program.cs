using System.Diagnostics;
using System.Globalization;
using System.Text;
using Optimisarr.Core.Calibration;
using Optimisarr.Core.Library;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Verification;

// VMAF model study: scores the same encodes under two VMAF models so Optimisarr's gates, tuned on
// vmaf_v0.6.1, can be restated on another model before anything switches to it. Every step is the
// server's own code — the probe, the adaptive sample windows, the sample encode command and the
// measurement service with its per-window alignment — so the numbers are the ones Optimisarr would
// have produced, with only the model changed. See docs/development/vmaf-model-study.md.

var options = StudyOptions.Parse(args);
if (options is null)
{
    Console.Error.WriteLine(StudyOptions.Usage);
    return 2;
}

Directory.CreateDirectory(options.Output);
if (options.ReportFrom is { } existing)
{
    // Restates the report from an earlier run's scores without measuring anything again.
    var measured = StudyRow.Parse(await File.ReadAllTextAsync(existing));
    await File.WriteAllTextAsync(Path.Combine(options.Output, "report.md"), StudyReport.Markdown(measured, options));
    Console.WriteLine($"Wrote a report for {measured.Count} measurements to {options.Output}");
    return 0;
}

var clips = Path.Combine(options.Output, "clips");
Directory.CreateDirectory(clips);
var probe = new MediaProbeService(options.Ffprobe);
var quality = new QualityScoreService(options.Ffmpeg);
var rows = new List<StudyRow>();

foreach (var source in options.Sources)
{
    var media = await probe.ProbeAsync(source, CancellationToken.None);
    if (!media.Success || media.Width is not > 0 || media.Height is not > 0)
    {
        Console.Error.WriteLine($"skip {Path.GetFileName(source)}: {media.Error ?? "no picture"}");
        continue;
    }
    if (media.IsHdr)
    {
        // The v1.0.16 models are SDR models; HDR needs its own study.
        Console.Error.WriteLine($"skip {Path.GetFileName(source)}: HDR");
        continue;
    }

    var duration = media.VideoDurationSeconds ?? media.DurationSeconds ?? 0;
    var windows = VmafWindowPlanner.PlanAdaptive(duration);
    var lead = media.VideoStartSeconds is { } video && media.ContainerStartSeconds is { } container ? video - container : (double?)null;
    var baselineModel = QualityScoreCommandBuilder.ModelVersionFor(media.Width.Value, media.Height.Value);
    var candidateModel = options.CandidateModelFor(media.Width.Value, media.Height.Value);

    foreach (var crf in options.Qualities)
    {
        for (var index = 0; index < windows.Count; index++)
        {
            var window = windows[index];
            var clip = Path.Combine(clips, $"{Slug(source)}-q{crf}-w{index}.mkv");
            if (!File.Exists(clip))
            {
                var spec = new TranscodeSpec(source, clip, options.Codec, crf, options.Preset, TonemapToSdr: false,
                    ClipStartSeconds: window.StartSeconds, ClipSeconds: window.DurationSeconds, VideoOnly: true);
                var encode = FfmpegCommandBuilder.Build(spec, threads: 0, options.Encoder);
                if (!await RunAsync(options.Ffmpeg, encode))
                {
                    Console.Error.WriteLine($"encode failed: {Path.GetFileName(clip)}");
                    continue;
                }
            }

            var bytes = new FileInfo(clip).Length;
            foreach (var model in new[] { baselineModel, candidateModel })
            {
                var context = new QualityMeasurementContext(media.Width.Value, media.Height.Value, false, false,
                    ReferenceStartSeconds: window.StartSeconds, ReferenceDurationSeconds: duration,
                    MeasureDurationSeconds: window.DurationSeconds, ReferenceFrameRate: media.VideoFrameRate,
                    ReferenceContainerLeadSeconds: lead, DistortedIsCutClip: true, ModelVersion: model);
                var result = await quality.MeasureAsync(source, clip, context, CancellationToken.None);
                var scores = result.Scores;
                rows.Add(new StudyRow(Path.GetFileName(source), index, options.Encoder, crf, bytes, model,
                    scores?.VmafHarmonicMean, scores?.VmafFifthPercentile, scores?.VmafMin, scores?.VmafMean,
                    scores?.FrameCount, result.Error));
                Console.WriteLine($"{Path.GetFileName(source)} q{crf} w{index} {model}: "
                    + (scores is null ? $"failed ({result.Error})" : $"harmonic {scores.VmafHarmonicMean:0.00}, p5 {scores.VmafFifthPercentile:0.00}, min {scores.VmafMin:0.00}"));
            }
        }
    }
}

await File.WriteAllTextAsync(Path.Combine(options.Output, "scores.csv"), StudyRow.Csv(rows));
await File.WriteAllTextAsync(Path.Combine(options.Output, "report.md"), StudyReport.Markdown(rows, options));
Console.WriteLine($"Wrote {rows.Count} measurements to {options.Output}");
return 0;

static string Slug(string path) =>
    new(Path.GetFileNameWithoutExtension(path).Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());

static async Task<bool> RunAsync(string executable, IReadOnlyList<string> arguments)
{
    using var process = new Process { StartInfo = new ProcessStartInfo(executable) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false } };
    foreach (var argument in arguments)
    {
        process.StartInfo.ArgumentList.Add(argument);
    }
    process.Start();
    var stderr = process.StandardError.ReadToEndAsync();
    var stdout = process.StandardOutput.ReadToEndAsync();
    await process.WaitForExitAsync();
    await Task.WhenAll(stderr, stdout);
    return process.ExitCode == 0;
}

internal sealed record StudyOptions(
    string Ffmpeg, string Ffprobe, string Output, IReadOnlyList<string> Sources, IReadOnlyList<int> Qualities,
    string Codec, string Encoder, string Preset, string? CandidateHd, string? CandidateUhd, string? ReportFrom)
{
    public const string DefaultCandidateHd = "vmaf_v1.0.16_3d0h";
    public const string DefaultCandidateUhd = "vmaf_v1.0.16_1d5h_2160";

    public const string Usage = """
        Usage: Optimisarr.VmafStudy --ffmpeg PATH --ffprobe PATH --out DIR [options] SOURCE...
          --ffmpeg PATH        FFmpeg built with libvmaf 3.2 or later (the v1.0.16 models are built in)
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
        string? ffmpeg = null, ffprobe = null, output = null, hd = null, uhd = null, reportFrom = null;
        string encoder = "libx265", preset = "medium";
        IReadOnlyList<int> qualities = [18, 22, 26, 30, 34];
        var sources = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--ffmpeg": ffmpeg = Next(); break;
                case "--ffprobe": ffprobe = Next(); break;
                case "--out": output = Next(); break;
                case "--qualities": qualities = Next().Split(',').Select(q => int.Parse(q, CultureInfo.InvariantCulture)).ToList(); break;
                case "--encoder": encoder = Next(); break;
                case "--preset": preset = Next(); break;
                case "--model-hd": hd = Next(); break;
                case "--model-uhd": uhd = Next(); break;
                case "--report-from": reportFrom = Next(); break;
                default: sources.Add(args[i]); break;
            }
        }
        var codec = encoder.Contains("264", StringComparison.Ordinal) ? "h264"
            : encoder.Contains("av1", StringComparison.Ordinal) ? "av1" : "hevc";
        if (reportFrom is not null && output is not null)
        {
            return new StudyOptions(ffmpeg ?? "", ffprobe ?? "", output, sources, qualities, codec, encoder, preset, hd, uhd, reportFrom);
        }
        return ffmpeg is null || ffprobe is null || output is null || sources.Count == 0
            ? null
            : new StudyOptions(ffmpeg, ffprobe, output, sources, qualities, codec, encoder, preset, hd, uhd, null);
    }
}

internal sealed record StudyRow(
    string Source, int Window, string Encoder, int Quality, long Bytes, string Model,
    double? Harmonic, double? FifthPercentile, double? Minimum, double? Mean, int? Frames, string? Error)
{
    public static string Csv(IEnumerable<StudyRow> rows)
    {
        var text = new StringBuilder("source,window,encoder,quality,bytes,model,harmonic,p5,min,mean,frames,error\n");
        foreach (var row in rows)
        {
            text.AppendLine(string.Join(',',
                Quote(row.Source), row.Window, row.Encoder, row.Quality, row.Bytes, row.Model,
                Number(row.Harmonic), Number(row.FifthPercentile), Number(row.Minimum), Number(row.Mean),
                row.Frames?.ToString(CultureInfo.InvariantCulture) ?? "", Quote(row.Error ?? "")));
        }
        return text.ToString();
    }

    /// <summary>Reads back what <see cref="Csv"/> wrote.</summary>
    public static List<StudyRow> Parse(string csv)
    {
        var rows = new List<StudyRow>();
        foreach (var line in csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            var f = Fields(line.TrimEnd('\r'));
            if (f.Count < 12) continue;
            static double? D(string v) => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
            rows.Add(new StudyRow(f[0], int.Parse(f[1], CultureInfo.InvariantCulture), f[2],
                int.Parse(f[3], CultureInfo.InvariantCulture), long.Parse(f[4], CultureInfo.InvariantCulture), f[5],
                D(f[6]), D(f[7]), D(f[8]), D(f[9]),
                int.TryParse(f[10], NumberStyles.Integer, CultureInfo.InvariantCulture, out var frames) ? frames : null,
                f[11].Length == 0 ? null : f[11]));
        }
        return rows;
    }

    private static List<string> Fields(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted && c == '"' && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
            else if (c == '"') quoted = !quoted;
            else if (c == ',' && !quoted) { fields.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }
        fields.Add(current.ToString());
        return fields;
    }

    private static string Number(double? value) => value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "";
    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}

internal static class StudyReport
{
    // Optimisarr's default gates on vmaf_v0.6.1: harmonic mean, fifth percentile, catastrophic floor.
    private static readonly (string Name, Func<StudyRow, double?> Score, double[] Gates)[] Metrics =
    [
        ("Harmonic mean", row => row.Harmonic, [95, 93, 90, 85]),
        ("Fifth percentile", row => row.FifthPercentile, [80, 75, 70]),
        ("Lowest frame", row => row.Minimum, [60, 45]),
    ];

    public static string Markdown(IReadOnlyList<StudyRow> rows, StudyOptions options)
    {
        var text = new StringBuilder("# VMAF model study\n\n");
        var sources = rows.Select(row => row.Source).Distinct().ToList();
        text.AppendLine(CultureInfo.InvariantCulture, $"{sources.Count} source(s), encoder `{options.Encoder}` preset `{options.Preset}`, qualities {string.Join(", ", options.Qualities)}, three 40-second sample windows each, measured with Optimisarr's own sample graph.\n");

        var models = rows.Select(row => row.Model).Distinct().ToList();
        foreach (var candidate in models.Where(model => !IsCurrent(model)))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"## {candidate} against vmaf_v0.6.1\n");
            foreach (var (name, score, gates) in Metrics)
            {
                var pairs = Pairs(rows, candidate, score);
                if (VmafModelComparison.Compare(pairs, gates) is not { } comparison)
                {
                    text.AppendLine(CultureInfo.InvariantCulture, $"**{name}:** not enough paired windows.\n");
                    continue;
                }
                text.AppendLine(CultureInfo.InvariantCulture,
                    $"**{name}** — {comparison.Count} windows, v1 = {comparison.Slope:0.###} × v0 {(comparison.Intercept < 0 ? "−" : "+")} {Math.Abs(comparison.Intercept):0.##}, correlation {comparison.Correlation:0.###}, mean difference {comparison.MeanDifference:+0.##;−0.##}\n");
                text.AppendLine("| v0 gate | v1 equivalent (fitted) | v1 equivalent (same share passing) | same verdict |");
                text.AppendLine("|---|---|---|---|");
                foreach (var gate in comparison.Thresholds)
                {
                    text.AppendLine(CultureInfo.InvariantCulture,
                        $"| {gate.BaselineThreshold:0.#} | {gate.LinearEquivalent:0.#} | {gate.RankEquivalent:0.#} | {gate.Agreements}/{gate.Windows} |");
                }
                text.AppendLine();
            }
        }

        text.AppendLine("## Shift by source (harmonic mean, candidate minus current)\n");
        text.AppendLine("A single fitted line can hide opposite shifts on different kinds of content. If these differ in sign, no one conversion of the gates is safe.\n");
        text.AppendLine("| source | windows | mean shift | range |");
        text.AppendLine("|---|---|---|---|");
        foreach (var source in rows.Select(row => row.Source).Distinct())
        {
            var shifts = rows
                .Where(row => row.Source == source && row.Harmonic is not null && !IsCurrent(row.Model))
                .Select(row => (row, baseline: rows.FirstOrDefault(other => other.Source == row.Source && other.Quality == row.Quality
                    && other.Window == row.Window && IsCurrent(other.Model) && other.Harmonic is not null)))
                .Where(pair => pair.baseline is not null)
                .Select(pair => pair.row.Harmonic!.Value - pair.baseline!.Harmonic!.Value)
                .ToList();
            if (shifts.Count > 0)
            {
                text.AppendLine(CultureInfo.InvariantCulture,
                    $"| {source} | {shifts.Count} | {shifts.Average():+0.00;−0.00} | {shifts.Min():+0.00;−0.00} to {shifts.Max():+0.00;−0.00} |");
            }
        }
        text.AppendLine();

        text.AppendLine("## Per source and quality (harmonic mean, both models)\n");
        text.AppendLine("| source | quality | bytes | " + string.Join(" | ", models) + " |");
        text.AppendLine("|---|---|---|" + string.Concat(models.Select(_ => "---|")));
        foreach (var group in rows.GroupBy(row => (row.Source, row.Quality)).OrderBy(g => g.Key.Source).ThenBy(g => g.Key.Quality))
        {
            var bytes = group.Where(row => row.Model == models[0]).Sum(row => row.Bytes);
            var cells = models.Select(model =>
            {
                var values = group.Where(row => row.Model == model && row.Harmonic is not null).Select(row => row.Harmonic!.Value).ToList();
                return values.Count == 0 ? "—" : values.Min().ToString("0.0", CultureInfo.InvariantCulture) + "–" + values.Max().ToString("0.0", CultureInfo.InvariantCulture);
            });
            text.AppendLine(CultureInfo.InvariantCulture, $"| {group.Key.Source} | {group.Key.Quality} | {bytes:N0} | {string.Join(" | ", cells)} |");
        }

        var failures = rows.Where(row => row.Error is not null).ToList();
        if (failures.Count > 0)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"\n{failures.Count} measurement(s) failed; see scores.csv.");
        }
        return text.ToString();
    }

    private static bool IsCurrent(string model) =>
        model == QualityScoreCommandBuilder.HdModelVersion || model == QualityScoreCommandBuilder.UhdModelVersion;

    private static List<PairedScore> Pairs(IReadOnlyList<StudyRow> rows, string candidate, Func<StudyRow, double?> score) =>
        rows.Where(row => row.Model == candidate && score(row) is not null)
            .Select(row => (row, baseline: rows.FirstOrDefault(other =>
                other.Source == row.Source && other.Quality == row.Quality && other.Window == row.Window
                && other.Model != candidate && score(other) is not null)))
            .Where(pair => pair.baseline is not null)
            .Select(pair => new PairedScore(score(pair.baseline!)!.Value, score(pair.row)!.Value))
            .ToList();
}
