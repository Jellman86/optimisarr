using System.Text.Json;

namespace Optimisarr.Core.Verification;

/// <summary>
/// Pure parser for the JSON log libvmaf writes (<c>log_fmt=json</c>). It reads the
/// <c>pooled_metrics</c> aggregates for VMAF and, when present, luma PSNR
/// (<c>psnr_y</c>) and SSIM (<c>float_ssim</c>). Malformed JSON or a log with no
/// VMAF yields null so the caller can treat "couldn't measure" distinctly from a
/// measured-but-low score.
/// </summary>
public static class QualityScoreParser
{
    public static QualityScores? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("pooled_metrics", out var pooled)
                || pooled.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var vmafMean = ReadMetric(pooled, "vmaf", "mean");
            var vmafHarmonicMean = ReadMetric(pooled, "vmaf", "harmonic_mean");
            var vmafMin = ReadMetric(pooled, "vmaf", "min");
            var frameScores = ReadFrameScores(document.RootElement);

            // Without any VMAF aggregate the log isn't a usable quality measurement.
            if (vmafMean is null && vmafHarmonicMean is null && vmafMin is null)
            {
                return null;
            }

            return new QualityScores(
                vmafMean,
                vmafHarmonicMean,
                vmafMin,
                ReadMetric(pooled, "psnr_y", "mean"),
                ReadMetric(pooled, "float_ssim", "mean"),
                VmafFifthPercentile: Percentile(frameScores, 0.05),
                FrameCount: frameScores.Count > 0 ? frameScores.Count : null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<double> ReadFrameScores(JsonElement root)
    {
        var scores = new List<double>();
        if (!root.TryGetProperty("frames", out var frames) || frames.ValueKind != JsonValueKind.Array)
        {
            return scores;
        }

        foreach (var frame in frames.EnumerateArray())
        {
            if (frame.TryGetProperty("metrics", out var metrics)
                && metrics.ValueKind == JsonValueKind.Object
                && metrics.TryGetProperty("vmaf", out var vmaf)
                && vmaf.ValueKind == JsonValueKind.Number
                && vmaf.TryGetDouble(out var score)
                && double.IsFinite(score))
            {
                scores.Add(score);
            }
        }

        scores.Sort();
        return scores;
    }

    // Linear interpolation matches the percentile convention used by common numerical tools and
    // keeps the value stable as clip length or frame rate changes.
    private static double? Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        if (sorted.Count == 0)
        {
            return null;
        }

        var rank = (sorted.Count - 1) * percentile;
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        if (lower == upper)
        {
            return sorted[lower];
        }

        var fraction = rank - lower;
        return sorted[lower] + ((sorted[upper] - sorted[lower]) * fraction);
    }

    private static double? ReadMetric(JsonElement pooled, string metric, string statistic)
    {
        if (pooled.TryGetProperty(metric, out var metricElement)
            && metricElement.ValueKind == JsonValueKind.Object
            && metricElement.TryGetProperty(statistic, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var parsed)
            && double.IsFinite(parsed))
        {
            return parsed;
        }

        return null;
    }
}
