using System.Globalization;
using System.Text;
using Optimisarr.Core.Calibration;
using Optimisarr.Core.Verification;

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
        text.AppendLine("Exploratory calibration only. V0 uses its established precision; V1 uses 10-bit SDR with actual encode parameters. Production gates are unchanged.\n");
        var sources = rows.Select(row => row.Source).Distinct().ToList();
        var encoders = string.Join(", ", rows.Select(r => $"`{r.Encoder}`").Distinct());
        var preset = options.ReportFrom is null ? $"preset `{options.Preset}`" : "presets recorded in the original run.json files";
        text.AppendLine(CultureInfo.InvariantCulture, $"{sources.Count} source(s), encoders {encoders}, {preset}, qualities {string.Join(", ", rows.Select(r => r.Quality).Distinct().Order())}, up to three 40-second sample windows each, measured with Optimisarr's own sample graph.\n");

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
                text.AppendLine("| v0 gate | v1 equivalent (fitted) | v1 rank cutoff (ties may differ) | same verdict |");
                text.AppendLine("|---|---|---|---|");
                foreach (var gate in comparison.Thresholds)
                {
                    var rank = pairs.All(p => p.Baseline < gate.BaselineThreshold)
                        ? "above observed maximum"
                        : gate.RankEquivalent.ToString("0.#", CultureInfo.InvariantCulture);
                    text.AppendLine(CultureInfo.InvariantCulture,
                        $"| {gate.BaselineThreshold:0.#} | {gate.LinearEquivalent:0.#} | {rank} | {gate.Agreements}/{gate.Windows} |");
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
            var shifts = StudyIntegrity.Pairs(rows).Where(pair => pair.Candidate.Source == source)
                .Select(pair => pair.Candidate.Harmonic!.Value - pair.Baseline.Harmonic!.Value).ToList();
            if (shifts.Count > 0)
            {
                text.AppendLine(CultureInfo.InvariantCulture,
                    $"| {source} | {shifts.Count} | {shifts.Average():+0.00;−0.00} | {shifts.Min():+0.00;−0.00} to {shifts.Max():+0.00;−0.00} |");
            }
        }
        text.AppendLine();

        text.AppendLine("## Per source and quality (harmonic mean, both models)\n");
        text.AppendLine("| source | encoder | quality | bytes | " + string.Join(" | ", models) + " |");
        text.AppendLine("|---|---|---|---|" + string.Concat(models.Select(_ => "---|")));
        foreach (var group in rows.GroupBy(row => (row.Source, row.Encoder, row.Quality)).OrderBy(g => g.Key.Source).ThenBy(g => g.Key.Quality))
        {
            var bytes = group.Where(row => row.Model == models[0]).Sum(row => row.Bytes);
            var cells = models.Select(model =>
            {
                var values = group.Where(row => row.Model == model && row.Harmonic is not null).Select(row => row.Harmonic!.Value).ToList();
                return values.Count == 0 ? "—" : values.Min().ToString("0.0", CultureInfo.InvariantCulture) + "–" + values.Max().ToString("0.0", CultureInfo.InvariantCulture);
            });
            text.AppendLine(CultureInfo.InvariantCulture, $"| {group.Key.Source} | {group.Key.Encoder} | {group.Key.Quality} | {bytes:N0} | {string.Join(" | ", cells)} |");
        }

        var failures = rows.Where(row => row.Error is not null).ToList();
        if (failures.Count > 0)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"\n{failures.Count} measurement(s) failed; see scores.csv.");
        }
        text.AppendLine("\n## Validation with each source held out\n");
        text.AppendLine("Thresholds are fitted on the other sources, then tested on the excluded source. A failed legacy gate accepted by the new gate is an unsafe acceptance relative to the current policy. This does not establish personal visual preference or independent title/genre coverage.\n");
        text.AppendLine("| Model | Metric / old gate | Proposed gate | Unsafe accepts | Rejected old passes | Result |");
        text.AppendLine("|---|---|---|---|---|---|");
        foreach (var candidate in models.Where(model => !IsCurrent(model)))
        {
            foreach (var (name, score, gates) in Metrics)
            {
                var paired = StudyIntegrity.Pairs(rows).Where(p => p.Candidate.Model == candidate)
                    .Select(p => new CalibrationScore(StudyIntegrity.SourceKey(p.Candidate.Source), score(p.Baseline)!.Value, score(p.Candidate)!.Value)).ToList();
                foreach (var gate in gates)
                {
                    var validation = VmafGateCalibration.Validate(paired, gate);
                    text.AppendLine(validation is null
                        ? $"| {candidate} | {name} / {gate} | — | — | — | Insufficient sources or pass/fail coverage |"
                        : FormattableString.Invariant($"| {candidate} | {name} / {gate} | {validation.ProposedThreshold:0.00} | {validation.UnsafeAccepts} | {validation.RejectedBaselinePasses}/{validation.BaselinePasses} | {(validation.Passed ? "Regression check passed; coverage review still required" : "Rejected")} |"));
                }
            }
        }
        return text.ToString();
    }

    private static bool IsCurrent(string model) => StudyIntegrity.IsBaseline(model);

    private static List<PairedScore> Pairs(IReadOnlyList<StudyRow> rows, string candidate, Func<StudyRow, double?> score) =>
        StudyIntegrity.Pairs(rows).Where(pair => pair.Candidate.Model == candidate)
            .Select(pair => new PairedScore(score(pair.Baseline)!.Value, score(pair.Candidate)!.Value)).ToList();
}
