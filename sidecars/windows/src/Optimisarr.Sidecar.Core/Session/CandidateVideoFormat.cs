using System.Globalization;
using System.Text.Json;

namespace Optimisarr.Sidecar.Core.Session;

/// <summary>Fills CAMBI's encode parameters from the candidate, never from the scaled reference.</summary>
public static class CandidateVideoFormat
{
    public const string Width = "{{encodedWidth}}";
    public const string Height = "{{encodedHeight}}";
    public const string BitDepth = "{{encodedBitDepth}}";

    public static bool Required(IReadOnlyList<string> arguments) =>
        arguments.Any(argument => argument.Contains(Width, StringComparison.Ordinal)
            || argument.Contains(Height, StringComparison.Ordinal) || argument.Contains(BitDepth, StringComparison.Ordinal));

    public static IReadOnlyList<string> ProbeArguments(string candidate) =>
        ["-v", "error", "-show_streams", "-of", "json", candidate];

    public static IReadOnlyList<string>? Resolve(IReadOnlyList<string> arguments, string? probeJson)
    {
        if (!Required(arguments)) return arguments;
        if (string.IsNullOrWhiteSpace(probeJson) || probeJson.Length > 1024 * 1024) return null;
        try
        {
            using var document = JsonDocument.Parse(probeJson);
            if (!document.RootElement.TryGetProperty("streams", out var streams)
                || streams.ValueKind != JsonValueKind.Array) return null;
            foreach (var stream in streams.EnumerateArray())
            {
                if (!stream.TryGetProperty("codec_type", out var kind) || kind.GetString() != "video"
                    || stream.TryGetProperty("disposition", out var disposition)
                        && disposition.TryGetProperty("attached_pic", out var attached) && attached.GetInt32() == 1)
                    continue;
                if (!stream.TryGetProperty("width", out var w) || !w.TryGetInt32(out var width) || width is <= 0 or > 32768
                    || !stream.TryGetProperty("height", out var h) || !h.TryGetInt32(out var height) || height is <= 0 or > 32768
                    || !stream.TryGetProperty("pix_fmt", out var pixel)) return null;
                var depth = pixel.GetString() switch
                {
                    "yuv420p" or "yuvj420p" or "yuv422p" or "yuvj422p" or "yuv444p" or "yuvj444p" or "nv12" => 8,
                    "yuv420p10le" or "yuv422p10le" or "yuv444p10le" or "p010le" => 10,
                    _ => 0
                };
                if (depth == 0 || stream.TryGetProperty("bits_per_raw_sample", out var raw)
                    && (!int.TryParse(raw.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var explicitDepth)
                        || explicitDepth < 0 || explicitDepth > 0 && explicitDepth != depth)) return null;
                return arguments.Select(argument => argument.Replace(Width, width.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                    .Replace(Height, height.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                    .Replace(BitDepth, depth.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)).ToArray();
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException) { }
        return null;
    }
}
