using System.Text.Json;

namespace Optimisarr.Sidecar.Core.Capabilities;

internal static class V1ProbeEvidence
{
    public static bool HasFiniteFrameScores(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("frames", out var frames)
                || frames.ValueKind != JsonValueKind.Array || frames.GetArrayLength() == 0) return false;
            return frames.EnumerateArray().All(frame => frame.TryGetProperty("metrics", out var metrics)
                && metrics.TryGetProperty("vmaf", out var vmaf) && vmaf.TryGetDouble(out var score)
                && double.IsFinite(score) && score is >= 0 and <= 100);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException) { return false; }
    }
}
