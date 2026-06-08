using Optimisarr.Core.Tools;

namespace Optimisarr.Core.Queue;

public sealed record EncoderSelection(bool Succeeded, string? EncoderName, string? Error)
{
    public static EncoderSelection Success(string encoderName) => new(true, encoderName, null);
    public static EncoderSelection Failure(string error) => new(false, null, error);
}

public static class EncoderSelector
{
    public static EncoderSelection Select(
        string targetCodec,
        EncoderMode mode,
        IReadOnlyList<EncoderCapability> capabilities)
    {
        var codec = NormaliseCodec(targetCodec);
        if (codec is null)
        {
            return EncoderSelection.Failure($"No known encoder for target codec '{targetCodec}'.");
        }

        var preferredModes = mode switch
        {
            EncoderMode.Auto => new[] { "NVIDIA NVENC", "Intel QSV", "VAAPI", "CPU" },
            EncoderMode.Cpu => ["CPU"],
            EncoderMode.NvidiaNvenc => ["NVIDIA NVENC"],
            EncoderMode.IntelQsv => ["Intel QSV"],
            EncoderMode.Vaapi => ["VAAPI"],
            _ => ["CPU"]
        };

        foreach (var preferredMode in preferredModes)
        {
            var match = capabilities.FirstOrDefault(encoder =>
                encoder.Available
                && encoder.Codec.Equals(codec, StringComparison.OrdinalIgnoreCase)
                && encoder.Mode.Equals(preferredMode, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return EncoderSelection.Success(match.Name);
            }
        }

        var modeName = mode == EncoderMode.Auto ? "Auto" : string.Join(", ", preferredModes);
        return EncoderSelection.Failure($"No available {modeName} encoder for target codec '{codec}'.");
    }

    private static string? NormaliseCodec(string codec) => codec.Trim().ToLowerInvariant() switch
    {
        "hevc" or "h265" or "x265" => "hevc",
        "h264" or "avc" or "x264" => "h264",
        "av1" => "av1",
        _ => null
    };
}

