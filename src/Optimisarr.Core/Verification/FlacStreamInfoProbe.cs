using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Optimisarr.Core.Tools;

namespace Optimisarr.Core.Verification;

/// <summary>Enriches metadata with only the STREAMINFO needed to resolve FLAC statistics durations.</summary>
public static class FlacStreamInfoProbe
{
    public const int MaximumTrackProbeCharacters = 16 * 1024;

    public static IReadOnlyList<string> Arguments(string path, int audioIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(audioIndex);
        return ["-v", "error", "-protocol_whitelist", "file", "-format_whitelist", "matroska,flac",
            "-select_streams", "a:" + audioIndex.ToString(CultureInfo.InvariantCulture),
            "-show_entries", "stream=index,codec_type,codec_name,sample_rate,extradata", "-show_data", "-of", "json", path];
    }

    public static Task<string> ProbeAsync(string ffprobe, string path, string metadata, CancellationToken token,
        bool writtenByFfmpeg = false) => EnrichAsync(metadata, async (index, cancellation) =>
        {
            var result = await BoundedToolProcess.RunAsync(ffprobe, Arguments(path, index), cancellation);
            return result.ExitCode == 0 ? result.Output : "";
        }, token, writtenByFfmpeg);

    public static async Task<string> EnrichAsync(string metadata, Func<int, CancellationToken, Task<string>> probeTrack,
        CancellationToken token, bool writtenByFfmpeg = false)
    {
        token.ThrowIfCancellationRequested();
        foreach (var index in RequiredAudioIndexes(metadata, writtenByFfmpeg))
            metadata = WithStreamInfo(metadata, index, await probeTrack(index, token));
        return metadata;
    }

    // Selection and enrichment are pure; process execution stays outside the parsing functions.
    public static IReadOnlyList<int> RequiredAudioIndexes(string metadata, bool writtenByFfmpeg = false)
    {
        if (writtenByFfmpeg || metadata.Length > 1024 * 1024) return [];
        try
        {
            using var doc = JsonDocument.Parse(metadata);
            return doc.RootElement.GetProperty("streams").EnumerateArray()
                .Where(s => s.GetProperty("codec_type").GetString() == "audio")
                .Select((track, index) => (track, index))
                .Where(p => NeedsStreamInfo(p.track)).Select(p => p.index).ToArray();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException) { return []; }
    }

    private static bool NeedsStreamInfo(JsonElement track)
    {
        if (!track.TryGetProperty("codec_name", out var codec) || codec.GetString() != "flac"
            || (track.TryGetProperty("duration", out var duration)
                && double.TryParse(duration.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            || !track.TryGetProperty("tags", out var tags)
            || AudioQualityInput.MatroskaTag(tags, "DURATION", true).ValueKind != JsonValueKind.String) return false;
        var writer = AudioQualityInput.MatroskaTag(tags, "_STATISTICS_WRITING_APP", true);
        return writer.ValueKind == JsonValueKind.String && (writer.GetString()!.StartsWith("mkvmerge", StringComparison.OrdinalIgnoreCase)
            || writer.GetString()!.StartsWith("mkvpropedit", StringComparison.OrdinalIgnoreCase));
    }

    public static string WithStreamInfo(string metadata, int audioIndex, string trackProbe)
    {
        if (metadata.Length > 1024 * 1024 || trackProbe.Length > MaximumTrackProbeCharacters) return metadata;
        try
        {
            if (JsonNode.Parse(metadata) is not JsonObject root || root["streams"] is not JsonArray streams
                || JsonNode.Parse(trackProbe)?["streams"] is not JsonArray selected) return metadata;
            var audio = streams.Where(s => s?["codec_type"]?.GetValue<string>() == "audio").ToArray();
            if (audioIndex < 0 || audioIndex >= audio.Length || selected.Count != 1 || selected[0] is not { } data) return metadata;
            var track = audio[audioIndex]!;
            if (track["codec_name"]?.GetValue<string>() != "flac" || data["codec_name"]?.GetValue<string>() != "flac"
                || data["codec_type"]?.GetValue<string>() != "audio"
                || !JsonNode.DeepEquals(track["sample_rate"], data["sample_rate"])
                || (track["index"] is not null && !JsonNode.DeepEquals(track["index"], data["index"]))
                || data["extradata"]?.GetValue<string>() is not { Length: <= 1024 } dump) return metadata;
            track["extradata"] = dump;
            var enriched = root.ToJsonString();
            return enriched.Length <= 1024 * 1024 ? enriched : metadata;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException) { return metadata; }
    }
}
