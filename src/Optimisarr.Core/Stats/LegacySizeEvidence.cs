using System.Text.Json;
using System.Text.RegularExpressions;

namespace Optimisarr.Core.Stats;

public readonly record struct SizePair(long OriginalBytes, long OutputBytes);

public sealed record ReportEvidence(SizePair? Sizes, double? VmafHarmonicMean)
{
    public static readonly ReportEvidence None = new(null, null);
}

/// <summary>
/// Reads the sizes and VMAF score out of a stored verification report. Reports written before
/// jobs recorded their source size carry it only in the size check's sentence, whose digit
/// grouping followed the server culture, so only the digits are trusted.
/// </summary>
public static partial class LegacySizeEvidence
{
    [GeneratedRegex(@"Original\s+(\d[\d.,'\s  ]*)\s+bytes,\s+output\s+(\d[\d.,'\s  ]*)\s+bytes", RegexOptions.CultureInvariant)]
    private static partial Regex SizeSentence();

    public static SizePair? TryParse(string? detail)
    {
        if (string.IsNullOrEmpty(detail)) return null;
        var match = SizeSentence().Match(detail);
        if (!match.Success) return null;
        return long.TryParse(Digits(match.Groups[1].Value), out var original)
            && long.TryParse(Digits(match.Groups[2].Value), out var output)
            ? new SizePair(original, output)
            : null;
    }

    public static ReportEvidence FromReportJson(string? reportJson)
    {
        if (string.IsNullOrWhiteSpace(reportJson)) return ReportEvidence.None;
        try
        {
            using var document = JsonDocument.Parse(reportJson);
            var root = document.RootElement;
            SizePair? sizes = null;
            if (root.TryGetProperty("checks", out var checks) && checks.ValueKind == JsonValueKind.Array)
            {
                foreach (var check in checks.EnumerateArray())
                {
                    if (check.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String
                        && TryParse(detail.GetString()) is { } found)
                    {
                        sizes = found;
                        break;
                    }
                }
            }

            double? vmaf = root.TryGetProperty("vmaf", out var evidence) && evidence.ValueKind == JsonValueKind.Object
                && evidence.TryGetProperty("scores", out var scores) && scores.ValueKind == JsonValueKind.Object
                && scores.TryGetProperty("vmafHarmonicMean", out var harmonic) && harmonic.ValueKind == JsonValueKind.Number
                    ? harmonic.GetDouble()
                    : null;
            return new ReportEvidence(sizes, vmaf);
        }
        catch (JsonException)
        {
            return ReportEvidence.None;
        }
    }

    private static string Digits(string value) => new(value.Where(char.IsAsciiDigit).ToArray());
}
