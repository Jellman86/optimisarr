using System.Globalization;
using System.Text.Json;

namespace Optimisarr.Sidecar.Core.Session;

/// <summary>
/// Reduces a libvmaf JSON log to the few numbers worth reading in a line of log output.
///
/// <para>The mean alone hides the failure that matters. A window whose frames are misaligned in
/// time scores perfectly well on average while individual frames score zero — which is what a
/// harmonic mean and a count of frames below a floor expose, and an average does not. A candidate
/// measured on this machine came back with a mean of 80.6 and a harmonic mean of 37.1, and nothing
/// in the log said which of its three windows had gone wrong.</para>
///
/// <para>The macOS sidecar has said this per window since the sample-alignment bug was found. This
/// is the same summary, so a window that misbehaves on either machine is described the same way.</para>
/// </summary>
public static class VmafLogSummary
{
    /// <summary>
    /// One line describing a window, or null when the log holds no frame scores to describe.
    /// </summary>
    public static string? Of(string json)
    {
        List<double> scores = [];
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("frames", out var frames)
                || frames.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var frame in frames.EnumerateArray())
            {
                if (frame.TryGetProperty("metrics", out var metrics)
                    && metrics.TryGetProperty("vmaf", out var vmaf)
                    && vmaf.TryGetDouble(out var score))
                {
                    scores.Add(score);
                }
            }
        }
        catch (JsonException)
        {
            // A log that cannot be read is not worth an exception: the measurement itself has
            // already been judged by FFmpeg's exit code, and this only describes it.
            return null;
        }

        if (scores.Count == 0)
        {
            return null;
        }

        var mean = scores.Average();
        // Guarded against zero: one zero-scoring frame would otherwise make the harmonic mean a
        // division by zero rather than the very low number it should be.
        var harmonic = scores.Count / scores.Sum(score => 1 / Math.Max(score, 0.01));
        var nearZero = scores.Count(score => score < 5);

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0} frames, mean {1:F2}, harmonic {2:F2}, min {3:F2}, {4} below 5",
            scores.Count, mean, harmonic, scores.Min(), nearZero);
    }
}
