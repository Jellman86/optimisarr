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
        IReadOnlyList<EncoderCapability> capabilities,
        int? sourceBitDepth = null)
    {
        var codec = NormaliseCodec(targetCodec);
        if (codec is null)
        {
            return EncoderSelection.Failure($"No known encoder for target codec '{targetCodec}'.");
        }

        if (codec == "h264" && sourceBitDepth is > 10)
        {
            return EncoderSelection.Failure(
                $"No supported H.264 encoder can preserve a {sourceBitDepth}-bit source. Choose HEVC or AV1.");
        }

        var requiresHighBitDepthCpuH264 = codec == "h264" && sourceBitDepth is > 8 and <= 10;
        if (requiresHighBitDepthCpuH264 && mode is not (EncoderMode.Auto or EncoderMode.Cpu))
        {
            return EncoderSelection.Failure(
                $"{ModeName(mode)} H.264 cannot preserve a {sourceBitDepth}-bit source. " +
                "Use Auto or CPU mode, or choose HEVC or AV1.");
        }

        string[] preferredModes = mode switch
        {
            EncoderMode.Auto when requiresHighBitDepthCpuH264 => ["CPU"],
            EncoderMode.Auto => ["NVIDIA NVENC", "Intel QSV", "VAAPI", "CPU"],
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

    private static string ModeName(EncoderMode mode) => mode switch
    {
        EncoderMode.NvidiaNvenc => "NVIDIA NVENC",
        EncoderMode.IntelQsv => "Intel QSV",
        EncoderMode.Vaapi => "VAAPI",
        _ => mode.ToString()
    };

    private static string? NormaliseCodec(string codec) => codec.Trim().ToLowerInvariant() switch
    {
        "hevc" or "h265" or "x265" => "hevc",
        "h264" or "avc" or "x264" => "h264",
        "av1" => "av1",
        _ => null
    };
}
