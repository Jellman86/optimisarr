using System.Globalization;
using System.Text.Json;
using Optimisarr.Core.Library;

namespace Optimisarr.Core.SuperResolution;

/// <summary>
/// The facts about an original that decide whether super-resolution can reproduce it, read from
/// one ffprobe of the file. The engine works on 8-bit RGB pictures of the first video stream. An
/// 8-bit or 10-bit 4:2:0 source is accepted: a 10-bit source is dithered into the engine's 8-bit
/// pictures and encoded back to 10-bit, which suits drawn content mastered in 8-bit and released in
/// 10-bit for compression. Anything else is refused before extraction: other chroma layouts or
/// depths, non-square pixels, rotation, or a second real video stream.
/// </summary>
public sealed record SuperResolutionSource(
    string? Refusal, double VideoStartSeconds, int AttachmentCount, int BitDepth = 8, SuperResolutionColour? Colour = null)
{
    public double? FrameRate { get; init; }

    private static readonly Dictionary<string, int> Supported420 = new()
    {
        ["yuv420p"] = 8, ["yuvj420p"] = 8, ["yuv420p10le"] = 10, ["yuv420p10be"] = 10,
    };

    public static SuperResolutionSource Parse(string ffprobeJson)
    {
        using var document = JsonDocument.Parse(ffprobeJson);
        var streams = document.RootElement.TryGetProperty("streams", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().ToList()
            : [];
        var videos = streams.Where(stream => Text(stream, "codec_type") == "video" && !IsAttachedPicture(stream)).ToList();
        var attachments = streams.Count(stream => Text(stream, "codec_type") == "attachment");
        if (videos.Count == 0)
        {
            return new("The file has no video stream to upscale.", 0, attachments);
        }

        var video = videos[0];
        // FFmpeg starts each input at its own earliest timestamp, so the picture's offset is measured
        // from the file's start, not from zero.
        var fileStart = document.RootElement.TryGetProperty("format", out var format) ? Seconds(Text(format, "start_time")) : 0;
        var start = Seconds(Text(video, "start_time")) - fileStart;
        var depth = Text(video, "pix_fmt") is { } pixelFormat && Supported420.TryGetValue(pixelFormat, out var bits) ? bits : 8;
        var colour = ColourOf(video);
        return new SuperResolutionSource(RefusalFor(video, videos.Count) ?? ColourRefusal(video), start, attachments, depth, colour)
        {
            FrameRate = MediaProbeService.Parse(ffprobeJson).VideoFrameRate,
        };
    }

    private static double Seconds(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? seconds : 0;

    private static readonly HashSet<string> WideGamut = ["bt2020nc", "bt2020c", "bt2020", "bt2020-10", "bt2020-12", "smpte2084", "arib-std-b67"];

    private static string? ColourRefusal(JsonElement video) =>
        new[] { Text(video, "color_space"), Text(video, "color_primaries"), Text(video, "color_transfer") }.Any(tag => tag is not null && WideGamut.Contains(tag))
            ? "Super-resolution supports standard-gamut (BT.709 or BT.601) colour only; this source uses wide-gamut or HDR colour."
            : null;

    private static SuperResolutionColour ColourOf(JsonElement video)
    {
        var space = Known(Text(video, "color_space"));
        var matrix = space switch
        {
            "bt709" => "bt709",
            "smpte170m" or "bt470bg" => "bt601",
            "fcc" => "fcc",
            "smpte240m" => "smpte240m",
            // Untagged video follows the usual convention: HD pictures are BT.709, SD pictures BT.601.
            _ => video.TryGetProperty("height", out var height) && height.TryGetInt32(out var rows) && rows < 720 ? "bt601" : "bt709",
        };
        var rangeTag = Known(Text(video, "color_range"));
        var fullRange = rangeTag == "pc" || rangeTag is null && Text(video, "pix_fmt") == "yuvj420p";
        return new(matrix, fullRange ? "pc" : "tv", Known(Text(video, "color_primaries")),
            Known(Text(video, "color_transfer")), space, RangeTagged: rangeTag is "tv" or "pc" || fullRange);
    }

    private static string? Known(string? tag) => tag is null or "unknown" or "reserved" ? null : tag;

    private static string? RefusalFor(JsonElement video, int videoStreams)
    {
        if (videoStreams > 1)
        {
            return "The file has more than one video stream; super-resolution upscales a single picture stream.";
        }
        // The engine would treat the combing between woven fields as detail and enlarge it.
        if (Text(video, "field_order") is { } fieldOrder && Domain.VideoFieldOrder.IsInterlaced(fieldOrder))
        {
            return $"Super-resolution does not support interlaced video; this source has field order {fieldOrder}.";
        }
        var pixelFormat = Text(video, "pix_fmt");
        if (pixelFormat is null || !Supported420.ContainsKey(pixelFormat))
        {
            return $"Super-resolution supports 8-bit and 10-bit 4:2:0 video; this source is {pixelFormat ?? "an unknown format"}. " +
                "Its frames would lose colour or detail on the way through the engine.";
        }
        if (Text(video, "sample_aspect_ratio") is { } aspect && aspect is not ("1:1" or "0:1"))
        {
            return $"Super-resolution needs square pixels; this source has a sample aspect ratio of {aspect}.";
        }
        if (Rotation(video) is { } rotation && rotation % 360 != 0)
        {
            return $"Super-resolution does not support rotated video; this source is rotated by {rotation} degrees.";
        }
        return null;
    }

    private static bool IsAttachedPicture(JsonElement stream) =>
        stream.TryGetProperty("disposition", out var disposition)
        && disposition.TryGetProperty("attached_pic", out var attached)
        && attached.ValueKind == JsonValueKind.Number && attached.GetInt32() == 1;

    private static double? Rotation(JsonElement video)
    {
        if (video.TryGetProperty("side_data_list", out var sideData) && sideData.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in sideData.EnumerateArray())
            {
                if (item.TryGetProperty("rotation", out var rotation) && rotation.TryGetDouble(out var degrees))
                {
                    return degrees;
                }
            }
        }
        return video.TryGetProperty("tags", out var tags) && tags.TryGetProperty("rotate", out var rotate)
            && double.TryParse(rotate.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var tagged)
            ? tagged
            : null;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
