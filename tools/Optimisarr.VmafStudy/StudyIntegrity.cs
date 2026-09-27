using System.Security.Cryptography;
using System.Text.Json;

internal static class StudyIntegrity
{
    public static string ClipKey(string sourceHash, string encoderHash, IReadOnlyList<string> arguments) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { sourceHash, encoderHash, arguments })));

    public static bool IsBaseline(string model) => model is StudyOptions.BaselineHd or StudyOptions.BaselineUhd;

    private static bool Valid(StudyRow row) => row.Error is null && row.Bytes > 0 && row.Frames > 0
        && new[] { row.Harmonic, row.FifthPercentile, row.Minimum, row.Mean }.All(v => v is { } n && double.IsFinite(n));

    public static IReadOnlyList<(StudyRow Baseline, StudyRow Candidate)> Pairs(IReadOnlyList<StudyRow> rows) =>
        rows.GroupBy(r => (r.Source, r.Window, r.Encoder, r.Quality, r.ClipSha256))
            .Where(g => g.Count() == 2 && g.All(Valid) && g.Count(r => IsBaseline(r.Model)) == 1
                && g.Select(r => r.Frames).Distinct().Count() == 1 && g.Select(r => r.Bytes).Distinct().Count() == 1)
            .Select(g => (g.Single(r => IsBaseline(r.Model)), g.Single(r => !IsBaseline(r.Model))))
            .ToList();

    public static bool Complete(IReadOnlyList<StudyRow> rows, int expected) =>
        expected > 0 && rows.Count == expected && Pairs(rows).Count * 2 == expected;
}
