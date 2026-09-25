using Optimisarr.Core.Queue;
using Optimisarr.Data;
using System.Text.RegularExpressions;

namespace Optimisarr.Api.Diagnostics;

/// <summary>All free-text diagnostic inputs pass a narrow allowlist before leaving the server.</summary>
internal static class DiagnosticSafeFields
{
    private static readonly IReadOnlySet<string> CheckNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "A/V sync", "Audio clipping (true peak)", "Audio codecs unchanged", "Audio fidelity",
        "Audio languages", "Audio loudness (EBU R128)", "Audio metadata and artwork",
        "Audio tracks", "Colour metadata", "Container unchanged", "Decode health", "Dimensions",
        "Duration", "HDR signal", "Image metadata (EXIF/ICC)", "Image quality (SSIM)",
        "Output readable", "Perceptual quality (VMAF)", "Picture", "Size saving", "Compression ceiling",
        "Source video timeline", "Subtitle languages", "Subtitle tracks", "Tail integrity",
        "Timestamp integrity", "Video stream", "Video structure"
    };

    private static readonly IReadOnlySet<string> Encoders = new HashSet<string>(StringComparer.Ordinal)
    {
        "libx264", "libx265", "libsvtav1", "h264_nvenc", "hevc_nvenc", "av1_nvenc",
        "h264_qsv", "hevc_qsv", "av1_qsv", "h264_vaapi", "hevc_vaapi", "av1_vaapi",
        "h264_videotoolbox", "hevc_videotoolbox"
    };

    private static readonly IReadOnlySet<string> Decoders = new HashSet<string>(StringComparer.Ordinal)
    {
        "videotoolbox", "cuda", "d3d11va", "qsv", "dxva2"
    };

    public static string CheckName(string? value) =>
        value is not null && CheckNames.Contains(value) ? value : "Other verification check";

    public static string? Encoder(string? value) =>
        value is not null && Encoders.Contains(value) ? value : null;

    public static string? Decoder(string? value) =>
        value is not null && Decoders.Contains(value) ? value : null;

    public static string? Version(string? value)
    {
        return value is { Length: <= 32 }
            && Regex.IsMatch(value, @"\A[v]?[0-9]{1,4}\.[0-9]{1,4}\.[0-9]{1,4}(?:\+[0-9a-f]{7,12})?\z",
                RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))
            ? value : null;
    }

    public static string OperatingSystem(string? value) => value?.ToLowerInvariant() switch
    {
        "windows" => "windows",
        "macos" => "macos",
        "linux" => "linux",
        _ => "unknown"
    };

    public static string Status(string? value) =>
        Enum.TryParse<JobStatus>(value, out var parsed) && Enum.IsDefined(parsed)
            ? parsed.ToString() : "Unknown";

    public static string EventReason(string? value)
    {
        if (value is "Job.StatusChanged" or "Retry.SoftwareDecode")
        {
            return value;
        }

        const string prefix = "Failure.";
        return value is not null && value.StartsWith(prefix, StringComparison.Ordinal)
            && Enum.TryParse<FailureCategory>(value[prefix.Length..], out var category)
            && Enum.IsDefined(category)
            ? $"Failure.{category}" : "Unclassified";
    }

    public static string AttemptOutcome(string? value) => value switch
    {
        "Rejected" => "Rejected",
        "Failed" => "Failed",
        "Completed" => "Completed",
        _ => "Unknown"
    };

    public static string AttemptReason(string? value) => value switch
    {
        "HardwareDecodeCorruption" => "HardwareDecodeCorruption",
        _ => "Unclassified"
    };

    public static string? Sha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit) ? value : null;

    public static string VerificationLocation(string? value) => value switch
    {
        "Server" => "Server",
        "Worker" => "Worker",
        _ => "Unknown"
    };
}
