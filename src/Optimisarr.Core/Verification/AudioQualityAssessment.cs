using System.Globalization;
using System.Text.Json;

namespace Optimisarr.Core.Verification;

public sealed record AudioQualityWindow(double StartSeconds, double DurationSeconds);

public static class AudioQualityWindowPlanner
{
    public static IReadOnlyList<AudioQualityWindow> Plan(double durationSeconds, bool soundtrack = false)
    {
        if (!double.IsFinite(durationSeconds) || durationSeconds < 1 || durationSeconds > 86400) return [];
        // Container duration can include encoder padding. Keep soundtrack samples clear of that boundary.
        if (soundtrack) durationSeconds -= Math.Min(0.1, durationSeconds - 1);
        var duration = Math.Floor(durationSeconds * 48000) / 48000;
        if (duration <= 90)
        {
            var count = (int)Math.Ceiling(duration / 30);
            var frames = (long)Math.Floor(duration * 48000);
            var length = frames / count;
            var remainder = frames % count;
            return Enumerable.Range(0, count)
                .Select(i => new AudioQualityWindow((i * length + Math.Min(i, remainder)) / 48000.0,
                    (length + (i < remainder ? 1 : 0)) / 48000.0))
                .ToArray();
        }
        return [new(0, 30), new(Math.Floor((duration - 30) * 24000) / 48000, 30), new(duration - 30, 30)];
    }
}

/// <param name="SourceChannels">Set when this stereo reference is the source track through the job's stereo
/// downmix; it binds the evidence to what was downmixed. Null for a track measured as it is.</param>
public sealed record AudioQualityInput(double DurationSeconds, int Channels, int SampleRate, string ChannelLayout, double ContainerLeadSeconds = 0,
    int? SourceChannels = null, string? SourceChannelLayout = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Downmixed => SourceChannels is not null;

    public static AudioQualityInput? Parse(string json)
        => ParseTrack(json, 0, standalone: true);

    /// <param name="writtenByFfmpeg">The file is an output this FFmpeg just muxed, so its plain DURATION tag is
    /// FFmpeg's own end timestamp even when stale statistics were copied from the source.</param>
    /// <param name="downmixToStereo">The job downmixes to stereo: a mono or known-layout surround track
    /// becomes a stereo reference recording its source channels; it is never a standalone input.</param>
    public static AudioQualityInput? ParseTrack(string json, int audioIndex, bool standalone = false, bool writtenByFfmpeg = false,
        bool downmixToStereo = false)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var streams = doc.RootElement.GetProperty("streams").EnumerateArray().ToArray();
            var audio = streams.Where(s => s.GetProperty("codec_type").GetString() == "audio").ToArray();
            if (audioIndex < 0 || audioIndex >= audio.Length || (standalone && (audio.Length != 1 || streams.Any(s => s.GetProperty("codec_type").GetString() == "video"
                && (!s.TryGetProperty("disposition", out var d) || !d.TryGetProperty("attached_pic", out var p) || p.GetInt32() != 1))))) return null;
            var track = audio[audioIndex];
            var channels = track.GetProperty("channels").GetInt32();
            var sampleRate = int.Parse(track.GetProperty("sample_rate").GetString()!, CultureInfo.InvariantCulture);
            var durationText = track.TryGetProperty("duration", out var t) && t.GetString() != "N/A" ? t.GetString() : null;
            double duration;
            if (!double.TryParse(durationText, NumberStyles.Float, CultureInfo.InvariantCulture, out duration))
            {
                var format = doc.RootElement.GetProperty("format");
                var encoder = format.TryGetProperty("tags", out var formatTags)
                    ? formatTags.EnumerateObject().FirstOrDefault(p => p.Name.Equals("encoder", StringComparison.OrdinalIgnoreCase)).Value : default;
                var muxedByFfmpeg = encoder.ValueKind == JsonValueKind.String && encoder.GetString()!.StartsWith("Lavf", StringComparison.OrdinalIgnoreCase);
                // FFmpeg writes a plain DURATION, which wins over any stale suffixed copy. A file with only
                // suffixed tags had them rewritten later (mkvmerge, or mkvpropedit after FFmpeg), except
                // in an output this FFmpeg just wrote, where they can only be copies of the source's.
                var suffixed = !writtenByFfmpeg;
                if (!standalone && track.TryGetProperty("tags", out var tags)
                    && MatroskaTag(tags, "DURATION", suffixed) is { ValueKind: JsonValueKind.String } end)
                {
                    var parts = end.GetString()!.Split(':');
                    if (parts.Length != 3 || !int.TryParse(parts[0], out var hours) || !int.TryParse(parts[1], out var minutes)
                        || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                        || hours < 0 || minutes is < 0 or > 59 || seconds is < 0 or >= 60
                        || !track.TryGetProperty("start_time", out var start)
                        || !double.TryParse(start.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var began)) return null;
                    var statistics = MatroskaTag(tags, "_STATISTICS_WRITING_APP", suffixed);
                    var statisticsLength = !writtenByFfmpeg && statistics.ValueKind == JsonValueKind.String
                        && (statistics.GetString()!.StartsWith("mkvmerge", StringComparison.OrdinalIgnoreCase)
                            || statistics.GetString()!.StartsWith("mkvpropedit", StringComparison.OrdinalIgnoreCase));
                    // FFmpeg can retain stale statistics, or mkvpropedit can refresh them after muxing.
                    if (statisticsLength && muxedByFfmpeg && began != 0) return null;
                    // MKVToolNix statistics store elapsed time; FFmpeg rewrites DURATION as an end timestamp.
                    duration = hours * 3600.0 + minutes * 60 + seconds - (statisticsLength ? 0 : began);
                    if (statisticsLength && track.TryGetProperty("codec_name", out var codec) && codec.GetString() == "flac")
                    {
                        // Matroska statistics may stop at the last packet's start. STREAMINFO carries
                        // the exact sample count; never guess a frame or widen the truncation tolerance.
                        if (FlacDuration(track, sampleRate, duration) is not { } exact) return null;
                        duration = exact;
                    }
                }
                else if (!standalone || !double.TryParse(doc.RootElement.GetProperty("format").GetProperty("duration").GetString(),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out duration)) return null;
            }
            if (AudioQualityWindowPlanner.Plan(duration).Count == 0 || sampleRate is < 8000 or > 384000) return null;
            var layout = track.TryGetProperty("channel_layout", out var l) ? l.GetString() : null;
            int? sourceChannels = null;
            string? sourceLayout = null;
            if (downmixToStereo && !standalone && channels != 2)
            {
                // A surround downmix follows the layout's own matrix; without a layout it would be a guess.
                if (channels == 1 ? !string.IsNullOrEmpty(layout) && layout != "mono" : channels is < 3 or > 8 || string.IsNullOrWhiteSpace(layout))
                    return null;
                (sourceChannels, sourceLayout, channels, layout) = (channels, channels == 1 ? "mono" : layout, 2, null);
            }
            if (channels is not (1 or 2)) return null;
            var expectedLayout = channels == 1 ? "mono" : "stereo";
            if (!string.IsNullOrEmpty(layout) && layout != expectedLayout) return null;
            var lead = 0.0;
            if (!standalone)
            {
                static double Start(JsonElement value) => value.TryGetProperty("start_time", out var t) && t.GetString() != "N/A"
                    ? double.Parse(t.GetString()!, CultureInfo.InvariantCulture) : throw new FormatException("Unknown picture timeline.");
                var video = streams.FirstOrDefault(s => s.GetProperty("codec_type").GetString() == "video"
                    && (!s.TryGetProperty("disposition", out var d) || !d.TryGetProperty("attached_pic", out var p) || p.GetInt32() != 1));
                if (video.ValueKind != JsonValueKind.Undefined)
                    lead = Start(video) - Start(doc.RootElement.GetProperty("format"));
                if (!double.IsFinite(lead) || lead is < 0 or > 0.1) return null;
            }
            return new(duration, channels, sampleRate, expectedLayout, lead, sourceChannels, sourceLayout);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or ArgumentNullException)
        { return null; }
    }

    private static double? FlacDuration(JsonElement track, int sampleRate, double statisticsDuration)
    {
        if (sampleRate is < 8000 or > 384000 || !track.TryGetProperty("extradata", out var data)
            || data.ValueKind != JsonValueKind.String || data.GetString() is not { Length: <= 1024 } dump) return null;
        var hex = string.Concat(dump.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
        {
            var colon = line.IndexOf(':');
            if (colon < 0) return "";
            var fields = line[(colon + 1)..].TrimStart();
            var ascii = fields.IndexOf("  ", StringComparison.Ordinal);
            return (ascii < 0 ? fields : fields[..ascii]).Replace(" ", "");
        }));
        if (hex.Length != 68) return null;
        var bytes = Convert.FromHexString(hex);
        var minimumBlock = (bytes[0] << 8) | bytes[1];
        var maximumBlock = (bytes[2] << 8) | bytes[3];
        var info = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(10, 8));
        var rate = (int)(info >> 44);
        var samples = info & 0xfffffffffUL;
        if (rate != sampleRate || samples == 0 || minimumBlock < 16 || maximumBlock < minimumBlock) return null;
        var duration = samples / (double)sampleRate;
        // Allow one omitted packet and Matroska's millisecond timestamp rounding, not stale headers.
        var missing = duration - statisticsDuration;
        return missing >= -0.001 && missing <= maximumBlock / (double)sampleRate + 0.001 ? duration : null;
    }

    // The plain tag, or else one agreed language-suffixed copy such as mkvmerge's DURATION-eng.
    private static JsonElement MatroskaTag(JsonElement tags, string name, bool allowLanguageSuffix)
    {
        var plain = tags.EnumerateObject().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
        if (plain.ValueKind != JsonValueKind.Undefined || !allowLanguageSuffix) return plain;
        var copies = tags.EnumerateObject().Where(p => p.Name.StartsWith(name + "-", StringComparison.OrdinalIgnoreCase)).Select(p => p.Value).ToArray();
        return copies.Length > 0 && copies.All(c => c.ValueKind == JsonValueKind.String) && copies.Select(c => c.GetString()).Distinct().Count() == 1
            ? copies[0] : default;
    }

    public static string? Incompatibility(AudioQualityInput reference, AudioQualityInput candidate) =>
        reference.Channels != candidate.Channels || reference.ChannelLayout != candidate.ChannelLayout
            ? "The candidate's channels do not match the reference."
            : Math.Abs(reference.DurationSeconds - candidate.DurationSeconds) > 0.1
                ? "The candidate's duration differs by more than the assessment's 100 ms tolerance."
                : null;
}

public static class AudioQualityCommandBuilder
{
    private const string AudioFormats = "wav,flac,mp3,aac,ogg,mov,matroska,asf,aiff,ape,wv,dsf,iff";
    private const string SoundtrackFormats = AudioFormats + ",avi,mpeg,mpegts,flv";

    public static IReadOnlyList<string> Probe(string source, bool soundtrack = false) =>
        ["-v", "error", .. InputOptions(soundtrack), "-show_streams", "-show_format", "-show_data", "-of", "json", source];

    private static IReadOnlyList<string> InputOptions(bool soundtrack) =>
        // Hashing a playlist does not bind its referenced media. Restrict the demuxer
        // before it opens the input, including during the very first qualification.
        ["-protocol_whitelist", "file", "-format_whitelist", soundtrack ? SoundtrackFormats : AudioFormats];

    public static IReadOnlyList<string> Decode(string source, string output, AudioQualityWindow window, int audioIndex = 0, double containerLeadSeconds = 0, bool soundtrack = false,
        bool downmixToStereo = false)
    {
        if (downmixToStereo && !soundtrack) throw new ArgumentException("Only a soundtrack reference is downmixed.", nameof(downmixToStereo));
        ArgumentOutOfRangeException.ThrowIfNegative(audioIndex);
        if (!double.IsFinite(containerLeadSeconds) || containerLeadSeconds is < 0 or > 0.1) throw new ArgumentOutOfRangeException(nameof(containerLeadSeconds));
        var seek = window.StartSeconds + containerLeadSeconds;
        // Opus needs up to 80 ms of decoder pre-roll. Near the origin, input seeking can
        // discard valid samples. Bound both trim timestamps so a late first sample cannot
        // move the window's end after timestamp normalization.
        var fromStart = soundtrack && seek <= 0.1;
        var inputSeek = soundtrack ? (fromStart ? 0 : seek) : Math.Max(0, seek - 10);
        var outputSeek = soundtrack ? 0 : seek - inputSeek;
        var frames = (long)Math.Round(window.DurationSeconds * 48000);
        // Matroska timestamps round to milliseconds. Keep the existing 64-frame rounding
        // allowance at the end, then cap actual samples; never fill a missing start with later audio.
        var end = seek + window.DurationSeconds + 64 / 48000.0;
        var trim = fromStart
            ? $"atrim=start={seek.ToString("0.########", CultureInfo.InvariantCulture)}:end={end.ToString("0.########", CultureInfo.InvariantCulture)},asetpts=PTS-STARTPTS,"
            : "";
        return [
        "-nostdin", "-hide_banner", "-nostats", "-v", "error", "-xerror", "-n",
        // Standalone audio uses output seeking; soundtracks trim on the demuxer-relative picture clock.
        .. inputSeek == 0 ? Array.Empty<string>() : ["-ss", inputSeek.ToString("0.########", CultureInfo.InvariantCulture)],
        .. InputOptions(soundtrack), "-i", source,
        .. outputSeek == 0 ? Array.Empty<string>() : ["-ss", outputSeek.ToString("0.########", CultureInfo.InvariantCulture)],
        "-map", $"0:a:{audioIndex.ToString(CultureInfo.InvariantCulture)}", "-t", (window.DurationSeconds + (soundtrack ? 0.1 : 0)).ToString("0.########", CultureInfo.InvariantCulture),
        "-vn", "-sn", "-dn", "-ar", "48000",
        .. soundtrack ? new[] { "-af", $"aresample=48000,{trim}atrim=end_sample={frames.ToString(CultureInfo.InvariantCulture)}" } : [],
        .. downmixToStereo ? Queue.FfmpegCommandBuilder.StereoDownmixArguments : [],
        "-c:a", "pcm_f32le", "-fs", "11520004", "-f", "f32le", output
        ];
    }
}

public sealed record AudioQualityDistances(int Frames, IReadOnlyList<double> ChannelDistances)
{
    public double? WorstChannelDistance => ChannelDistances is { Count: > 0 } ? ChannelDistances.Max() : null;
}

public static class AudioQualityResultParser
{
    public const string Revision = "f9e7364df2f6a41f761f513b7ea6be7e2d6f2ce3";
    public const string Preparation = "audio-f32le-48k-native-defaults-v1";
    public const string SoundtrackPreparation = "audio-f32le-48k-video-timeline-v2";
    /// <summary>The reference is the source track through the job's stereo downmix.</summary>
    public const string SoundtrackDownmixPreparation = "audio-f32le-48k-video-timeline-stereo-downmix-v2";

    public static bool IsSoundtrack(string preparation) => preparation is SoundtrackPreparation or SoundtrackDownmixPreparation;

    public static AudioQualityDistances? Parse(string json, int channels, double durationSeconds)
    {
        if (json.Length > 8192 || channels is not (1 or 2) || !double.IsFinite(durationSeconds) || durationSeconds is < 1 or > 30) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() != 1)) return null;
            if (root.GetProperty("schema").GetInt32() != 1 || root.GetProperty("metric").GetString() != "zimtohrli"
                || root.GetProperty("revision").GetString() != Revision || root.GetProperty("sampleRate").GetInt32() != 48000
                || root.GetProperty("channels").GetInt32() != channels || Math.Abs(root.GetProperty("fullScaleSineDb").GetDouble() - 78.3) > 0.0001) return null;
            var frames = root.GetProperty("frames").GetInt32();
            if (Math.Abs(frames - durationSeconds * 48000) > 64) return null;
            var values = root.GetProperty("distances").EnumerateArray().Select(v => v.GetDouble()).ToArray();
            if (values.Length != channels || values.Any(v => !double.IsFinite(v) || v is < 0 or > 1)) return null;
            return new(frames, values);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { return null; }
    }
}
