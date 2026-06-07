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
    string? Error)
{
    public static MediaProbeResult Failure(string error) =>
        new(false, null, null, null, null, null, Array.Empty<string>(), 0, 0, error);
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
        var audioCodecs = new List<string>();
        var subtitleCount = 0;

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
                        break;
                    case "audio":
                        audioCodecs.Add(codecName ?? "unknown");
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
            null);
    }

    private static string? FirstLine(string value) =>
        value.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
}
