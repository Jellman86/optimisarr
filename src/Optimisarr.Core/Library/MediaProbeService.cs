using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Optimisarr.Core.Library;

public sealed record MediaProbeResult(
    bool Success,
    string? Container,
    double? DurationSeconds,
    string? VideoCodec,
    int? Width,
    int? Height,
    IReadOnlyList<string> AudioCodecs,
    int AudioTrackCount,
    int SubtitleTrackCount,
    bool IsHdr,
    int MaxAudioChannels,
    int MaxAudioSampleRate,
    string? Error)
{
    public static MediaProbeResult Failure(string error) =>
        new(false, null, null, null, null, null, Array.Empty<string>(), 0, 0, false, 0, 0, error);
}

/// <summary>
/// Inspects media files with ffprobe using machine-readable JSON output.
/// ffprobe is invoked through an explicit argument list, never a shell string.
/// </summary>
public sealed class MediaProbeService
{
    private const string FfprobeCommand = "ffprobe";

    public async Task<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return MediaProbeResult.Failure($"File does not exist: {path}");
        }

        string stdout;
        string stderr;
        int exitCode;

        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = FfprobeCommand,
                ArgumentList =
                {
                    "-v", "error",
                    "-print_format", "json",
                    "-show_format",
                    "-show_streams",
                    path
                },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            process.Start();

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            stdout = await stdoutTask;
            stderr = await stderrTask;
            exitCode = process.ExitCode;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return MediaProbeResult.Failure(ex.Message);
        }

        if (exitCode != 0)
        {
            var message = FirstLine(stderr) ?? $"ffprobe exited with code {exitCode}";
            return MediaProbeResult.Failure(message);
        }

        try
        {
            return Parse(stdout);
        }
        catch (JsonException ex)
        {
            return MediaProbeResult.Failure($"Could not parse ffprobe output: {ex.Message}");
        }
    }

    internal static MediaProbeResult Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        string? container = null;
        double? duration = null;

        if (root.TryGetProperty("format", out var format))
        {
            if (format.TryGetProperty("format_name", out var formatName))
            {
                container = formatName.GetString();
            }

            if (format.TryGetProperty("duration", out var durationElement) &&
                durationElement.ValueKind == JsonValueKind.String &&
                double.TryParse(durationElement.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                duration = parsed;
            }
        }

        string? videoCodec = null;
        int? width = null;
        int? height = null;
        var isHdr = false;
        var audioCodecs = new List<string>();
        var subtitleCount = 0;
        var maxAudioChannels = 0;
        var maxAudioSampleRate = 0;

        if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var stream in streams.EnumerateArray())
            {
                var codecType = stream.TryGetProperty("codec_type", out var typeElement)
                    ? typeElement.GetString()
                    : null;
                var codecName = stream.TryGetProperty("codec_name", out var nameElement)
                    ? nameElement.GetString()
                    : null;

                switch (codecType)
                {
                    case "video" when videoCodec is null:
                        videoCodec = codecName;
                        if (stream.TryGetProperty("width", out var w) && w.TryGetInt32(out var widthValue))
                        {
                            width = widthValue;
                        }
                        if (stream.TryGetProperty("height", out var h) && h.TryGetInt32(out var heightValue))
                        {
                            height = heightValue;
                        }
                        isHdr = IsHdrVideoStream(stream);
                        break;
                    case "audio":
                        audioCodecs.Add(codecName ?? "unknown");
                        if (stream.TryGetProperty("channels", out var ch) && ch.TryGetInt32(out var channels))
                        {
                            maxAudioChannels = Math.Max(maxAudioChannels, channels);
                        }
                        if (stream.TryGetProperty("sample_rate", out var sr)
                            && sr.ValueKind == JsonValueKind.String
                            && int.TryParse(sr.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sampleRate))
                        {
                            maxAudioSampleRate = Math.Max(maxAudioSampleRate, sampleRate);
                        }
                        break;
                    case "subtitle":
                        subtitleCount++;
                        break;
                }
            }
        }

        return new MediaProbeResult(
            true,
            container,
            duration,
            videoCodec,
            width,
            height,
            audioCodecs,
            audioCodecs.Count,
            subtitleCount,
            isHdr,
            maxAudioChannels,
            maxAudioSampleRate,
            null);
    }

    // HDR10/HDR10+ and HLG are signalled by the transfer characteristics; Dolby
    // Vision is carried as stream side data even when the transfer is SDR-tagged.
    private static bool IsHdrVideoStream(JsonElement videoStream)
    {
        if (videoStream.TryGetProperty("color_transfer", out var transferElement) &&
            transferElement.GetString() is { } transfer &&
            (transfer is "smpte2084" or "arib-std-b67"))
        {
            return true;
        }

        if (videoStream.TryGetProperty("side_data_list", out var sideData) &&
            sideData.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in sideData.EnumerateArray())
            {
                if (entry.TryGetProperty("side_data_type", out var typeElement) &&
                    typeElement.GetString() is { } sideDataType &&
                    sideDataType.Contains("DOVI", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string? FirstLine(string value) =>
        value.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
}
