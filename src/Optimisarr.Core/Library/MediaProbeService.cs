using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Optimisarr.Core;
using Optimisarr.Core.Domain;

namespace Optimisarr.Core.Library;

/// <summary>One audio stream's shape, in the file's stream order (index = audio-relative position).</summary>
public sealed record AudioTrackInfo(string? Language, int Channels, int SampleRate, string? Codec);

public sealed record MediaProbeResult(
    bool Success,
    string? Container,
    double? DurationSeconds,
    string? VideoCodec,
    int? Width,
    int? Height,
    int? FrameCount,
    IReadOnlyList<string> AudioCodecs,
    IReadOnlyList<AudioTrackInfo> AudioTracks,
    int AudioTrackCount,
    int SubtitleTrackCount,
    bool HasImageSubtitles,
    bool IsHdr,
    bool IsDolbyVision,
    int MaxAudioChannels,
    int MaxAudioSampleRate,
    int? AudioBitrateKbps,
    string? ColorPrimaries,
    string? ColorTransfer,
    string? ColorSpace,
    double? VideoStartSeconds,
    double? AudioStartSeconds,
    string? OptimisedMarker,
    MediaKind MediaKind,
    string? PixelFormat,
    int? BitsPerRawSample,
    int AttachedPictureCount,
    IReadOnlyDictionary<string, string> FormatTags,
    bool? IsVariableFrameRate,
    string? VideoProfile,
    string? Error)
{
    public static MediaProbeResult Failure(string error) =>
        new(false, null, null, null, null, null, null, Array.Empty<string>(), Array.Empty<AudioTrackInfo>(),
            0, 0, false, false, false, 0, 0, null,
            null, null, null, null, null, null, MediaKind.Unknown, null, null, 0,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), null, null, error);
}

/// <summary>
/// Inspects media files with ffprobe using machine-readable JSON output.
/// ffprobe is invoked through an explicit argument list, never a shell string.
/// </summary>
public sealed class MediaProbeService
{
    private readonly string _ffprobe;

    public MediaProbeService(string? ffprobeCommand = null)
    {
        _ffprobe = string.IsNullOrWhiteSpace(ffprobeCommand) ? "ffprobe" : ffprobeCommand;
    }

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
                FileName = _ffprobe,
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
            return Parse(stdout, Path.GetExtension(path));
        }
        catch (JsonException ex)
        {
            return MediaProbeResult.Failure($"Could not parse ffprobe output: {ex.Message}");
        }
    }

    internal static MediaProbeResult Parse(string json, string? extension = null)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        string? container = null;
        double? duration = null;
        string? optimisedMarker = null;
        int? formatBitrateKbps = null;
        var formatTags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (root.TryGetProperty("format", out var format))
        {
            if (format.TryGetProperty("format_name", out var formatName))
            {
                container = formatName.GetString();
            }

            formatBitrateKbps = ReadBitrateKbps(format);

            if (format.TryGetProperty("duration", out var durationElement) &&
                durationElement.ValueKind == JsonValueKind.String &&
                double.TryParse(durationElement.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                duration = parsed;
            }

            optimisedMarker = ReadOptimisedMarker(format);
            if (format.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object)
            {
                foreach (var tag in tags.EnumerateObject())
                {
                    if (tag.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(tag.Value.GetString()))
                    {
                        formatTags[tag.Name] = tag.Value.GetString()!;
                    }
                }
            }
        }

        string? videoCodec = null;
        int? width = null;
        int? height = null;
        int? frameCount = null;
        var isHdr = false;
        var isDolbyVision = false;
        var hasRealVideoStream = false;
        var audioCodecs = new List<string>();
        var audioTracks = new List<AudioTrackInfo>();
        var subtitleCount = 0;
        var hasImageSubtitles = false;
        var maxAudioChannels = 0;
        var maxAudioSampleRate = 0;
        int? audioBitrateKbps = null;
        string? colorPrimaries = null;
        string? colorTransfer = null;
        string? colorSpace = null;
        double? videoStart = null;
        double? audioStart = null;
        string? pixelFormat = null;
        int? bitsPerRawSample = null;
        var attachedPictureCount = 0;
        bool? isVariableFrameRate = null;
        string? videoProfile = null;

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
                    // Embedded cover art (an attached picture, e.g. album art on an MP3) is a
                    // video stream but not real video; skip it so audio files aren't mistaken
                    // for movies and the captured video codec is the actual picture track.
                    case "video" when IsAttachedPicture(stream):
                        attachedPictureCount++;
                        break;
                    case "video":
                        hasRealVideoStream = true;
                        if (videoCodec is not null)
                        {
                            break;
                        }
                        videoCodec = codecName;
                        if (stream.TryGetProperty("width", out var w) && w.TryGetInt32(out var widthValue))
                        {
                            width = widthValue;
                        }
                        if (stream.TryGetProperty("height", out var h) && h.TryGetInt32(out var heightValue))
                        {
                            height = heightValue;
                        }
                        // nb_frames distinguishes a still image (1 / not reported) from an animated
                        // one (e.g. a GIF or animated WebP), which must not be treated as a photo.
                        // ffprobe reports it as a string and may omit it ("N/A").
                        if (stream.TryGetProperty("nb_frames", out var nf)
                            && nf.ValueKind == JsonValueKind.String
                            && int.TryParse(nf.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var frames))
                        {
                            frameCount = frames;
                        }
                        isHdr = IsHdrVideoStream(stream);
                        isDolbyVision = IsDolbyVisionStream(stream);
                        colorPrimaries = ReadString(stream, "color_primaries");
                        colorTransfer = ReadString(stream, "color_transfer");
                        colorSpace = ReadString(stream, "color_space");
                        pixelFormat = ReadString(stream, "pix_fmt");
                        bitsPerRawSample = ReadIntegerString(stream, "bits_per_raw_sample");
                        isVariableFrameRate = DetectVariableFrameRate(stream);
                        videoProfile = ReadString(stream, "profile");
                        videoStart = ReadStartTime(stream);
                        break;
                    case "audio":
                        audioCodecs.Add(codecName ?? "unknown");
                        // Vorbis/Opus commonly expose music tags on the audio stream rather than
                        // format.tags. Merge them into the verification view without overriding a
                        // container-level value when both are present.
                        if (stream.TryGetProperty("tags", out var audioTags)
                            && audioTags.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var tag in audioTags.EnumerateObject())
                            {
                                if (tag.Value.ValueKind == JsonValueKind.String
                                    && !string.IsNullOrWhiteSpace(tag.Value.GetString()))
                                {
                                    formatTags.TryAdd(tag.Name, tag.Value.GetString()!);
                                }
                            }
                        }
                        audioStart ??= ReadStartTime(stream);
                        var audioChannels = ReadChannels(stream);
                        maxAudioChannels = Math.Max(maxAudioChannels, audioChannels);
                        var audioSampleRate = ReadSampleRate(stream);
                        maxAudioSampleRate = Math.Max(maxAudioSampleRate, audioSampleRate);
                        if (ReadBitrateKbps(stream) is { } streamBitrate)
                        {
                            audioBitrateKbps = Math.Max(audioBitrateKbps ?? 0, streamBitrate);
                        }
                        audioTracks.Add(new AudioTrackInfo(
                            ReadLanguageTag(stream),
                            audioChannels,
                            audioSampleRate,
                            codecName));
                        break;
                    case "subtitle":
                        subtitleCount++;
                        if (SubtitleClassifier.IsImageBased(codecName))
                        {
                            hasImageSubtitles = true;
                        }
                        break;
                }
            }
        }

        // For an audio-only file the container bitrate is effectively the audio bitrate, so use
        // it when no per-stream bitrate was reported. A file with real video is excluded: its
        // container bitrate is dominated by the video track and tells us nothing about the audio.
        if (audioBitrateKbps is null && !hasRealVideoStream && audioCodecs.Count > 0)
        {
            audioBitrateKbps = formatBitrateKbps;
        }

        return new MediaProbeResult(
            true,
            container,
            duration,
            videoCodec,
            width,
            height,
            frameCount,
            audioCodecs,
            audioTracks,
            audioCodecs.Count,
            subtitleCount,
            hasImageSubtitles,
            isHdr,
            isDolbyVision,
            maxAudioChannels,
            maxAudioSampleRate,
            audioBitrateKbps,
            colorPrimaries,
            colorTransfer,
            colorSpace,
            videoStart,
            audioStart,
            optimisedMarker,
            MediaKindClassifier.Classify(extension, hasRealVideoStream, audioCodecs.Count > 0),
            pixelFormat,
            bitsPerRawSample,
            attachedPictureCount,
            formatTags,
            isVariableFrameRate,
            videoProfile,
            null);
    }

    // A cover-art / attached-picture stream is flagged by its disposition; it is a still
    // image carried alongside audio, not a real video track.
    private static bool IsAttachedPicture(JsonElement stream) =>
        stream.TryGetProperty("disposition", out var disposition)
        && disposition.ValueKind == JsonValueKind.Object
        && disposition.TryGetProperty("attached_pic", out var attachedPic)
        && attachedPic.TryGetInt32(out var flag)
        && flag == 1;

    // The optimisation fingerprint is a container-level tag (format.tags). ffprobe may
    // report the key in any case, so the match is case-insensitive; an empty value counts
    // as absent.
    private static string? ReadOptimisedMarker(JsonElement format)
    {
        if (!format.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var tag in tags.EnumerateObject())
        {
            if (string.Equals(tag.Name, OptimisationMarker.MetadataKey, StringComparison.OrdinalIgnoreCase)
                && tag.Value.ValueKind == JsonValueKind.String)
            {
                var value = tag.Value.GetString();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }

        return null;
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

    // Dolby Vision is carried as a DOVI configuration record in the stream's side data, and is also
    // signalled by the dvhe/dvh1/dav1/dvav codec tags on a remux. Either marks the stream as DV; the
    // RPU it depends on cannot survive a re-encode, so the source is left untouched by default.
    private static bool IsDolbyVisionStream(JsonElement videoStream)
    {
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

        return videoStream.TryGetProperty("codec_tag_string", out var tagElement) &&
            tagElement.GetString() is { } tag &&
            (tag.StartsWith("dvh", StringComparison.OrdinalIgnoreCase) ||
             tag.StartsWith("dav", StringComparison.OrdinalIgnoreCase) ||
             tag.StartsWith("dvav", StringComparison.OrdinalIgnoreCase));
    }

    private static string? ReadString(JsonElement stream, string property)
    {
        if (stream.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String)
        {
            var value = element.GetString();
            // ffprobe writes "unknown"/"reserved" for unspecified color metadata; treat as absent.
            return string.IsNullOrWhiteSpace(value) || value is "unknown" or "reserved" ? null : value;
        }

        return null;
    }

    private static int? ReadIntegerString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
        && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private static bool? DetectVariableFrameRate(JsonElement stream)
    {
        var nominal = ReadRational(stream, "r_frame_rate");
        var average = ReadRational(stream, "avg_frame_rate");
        if (nominal is not > 0 || average is not > 0)
        {
            return null;
        }

        // r_frame_rate and avg_frame_rate can differ minutely because of duration rounding.
        // Require a 0.1% divergence before treating it as positive VFR evidence.
        return Math.Abs(nominal.Value - average.Value) / nominal.Value > 0.001;
    }

    private static double? ReadRational(JsonElement element, string property)
    {
        var value = ReadString(element, property);
        if (value is null)
        {
            return null;
        }

        var parts = value.Split('/', 2);
        if (parts.Length != 2
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator)
            || denominator == 0)
        {
            return null;
        }

        return numerator / denominator;
    }

    // ffprobe reports bit_rate as a string of bits per second on both streams and the format.
    // Convert to whole kbps; a missing or non-numeric value (some VBR sources omit it) is absent.
    private static int? ReadBitrateKbps(JsonElement element)
    {
        if (element.TryGetProperty("bit_rate", out var bitRate)
            && bitRate.ValueKind == JsonValueKind.String
            && long.TryParse(bitRate.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bitsPerSecond)
            && bitsPerSecond > 0)
        {
            return (int)(bitsPerSecond / 1000);
        }

        return null;
    }

    private static double? ReadStartTime(JsonElement stream)
    {
        if (stream.TryGetProperty("start_time", out var element)
            && element.ValueKind == JsonValueKind.String
            && double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        return null;
    }

    // Containers tag streams with ISO 639 codes in any case; normalise to lower case so
    // language comparisons are stable. A blank tag means the language is unknown.
    private static string? ReadLanguageTag(JsonElement stream)
    {
        if (!stream.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var tag in tags.EnumerateObject())
        {
            if (string.Equals(tag.Name, "language", StringComparison.OrdinalIgnoreCase)
                && tag.Value.ValueKind == JsonValueKind.String)
            {
                var value = tag.Value.GetString()?.Trim();
                return string.IsNullOrEmpty(value) ? null : value.ToLowerInvariant();
            }
        }

        return null;
    }

    private static int ReadChannels(JsonElement stream) =>
        stream.TryGetProperty("channels", out var channels) && channels.TryGetInt32(out var value)
            ? value
            : 0;

    private static int ReadSampleRate(JsonElement stream) =>
        stream.TryGetProperty("sample_rate", out var sampleRate)
            && sampleRate.ValueKind == JsonValueKind.String
            && int.TryParse(sampleRate.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;

    private static string? FirstLine(string value) =>
        value.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
}
