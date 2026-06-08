namespace Optimisarr.Core.Tools;

/// <summary>Pure parser for FFmpeg capability output.</summary>
public static class HardwareCapabilityParser
{
    private static readonly EncoderDefinition[] KnownEncoders =
    [
        new("libx264", "h264", "CPU"),
        new("libx265", "hevc", "CPU"),
        new("libsvtav1", "av1", "CPU"),
        new("h264_nvenc", "h264", "NVIDIA NVENC"),
        new("hevc_nvenc", "hevc", "NVIDIA NVENC"),
        new("av1_nvenc", "av1", "NVIDIA NVENC"),
        new("h264_qsv", "h264", "Intel QSV"),
        new("hevc_qsv", "hevc", "Intel QSV"),
        new("av1_qsv", "av1", "Intel QSV"),
        new("h264_vaapi", "h264", "VAAPI"),
        new("hevc_vaapi", "hevc", "VAAPI"),
        new("av1_vaapi", "av1", "VAAPI")
    ];

    public static IReadOnlyList<string> ParseHardwareAccelerators(string output)
    {
        var accelerators = new List<string>();
        var inList = false;

        foreach (var rawLine in output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (rawLine.Equals("Hardware acceleration methods:", StringComparison.OrdinalIgnoreCase))
            {
                inList = true;
                continue;
            }

            if (inList)
            {
                accelerators.Add(rawLine);
            }
        }

        return accelerators;
    }

    /// <summary>
    /// Maps FFmpeg's <c>-encoders</c> listing to known encoders. A hardware encoder is
    /// only reported available when the underlying hardware is actually usable here, not
    /// merely compiled into FFmpeg: NVENC requires a working NVIDIA runtime, and QSV/VAAPI
    /// require a DRI render device. Listing the encoder is necessary but not sufficient —
    /// FFmpeg ships with these encoders built in and will list them even on a GPU-less host.
    /// </summary>
    public static IReadOnlyList<EncoderCapability> ParseEncoders(
        string output,
        bool nvidiaRuntimeAvailable,
        bool driDeviceAvailable)
    {
        var listed = output
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(ParseEncoderName)
            .Where(name => name is not null)
            .ToHashSet(StringComparer.Ordinal);

        return KnownEncoders
            .Select(encoder => new EncoderCapability(
                encoder.Name,
                encoder.Codec,
                encoder.Mode,
                listed.Contains(encoder.Name)
                    && HardwarePresent(encoder.Mode, nvidiaRuntimeAvailable, driDeviceAvailable)))
            .ToList();
    }

    private static bool HardwarePresent(string mode, bool nvidiaRuntimeAvailable, bool driDeviceAvailable) => mode switch
    {
        "NVIDIA NVENC" => nvidiaRuntimeAvailable,
        "Intel QSV" or "VAAPI" => driDeviceAvailable,
        _ => true
    };

    private static string? ParseEncoderName(string line)
    {
        var columns = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return columns.Length >= 2 && columns[0].Length >= 6
            ? columns[1]
            : null;
    }

    private sealed record EncoderDefinition(string Name, string Codec, string Mode);
}
