using System.Globalization;
using System.Text.Json;

namespace Optimisarr.Core.SuperResolution;

/// <summary>
/// The facts about an original that decide whether super-resolution can reproduce it, read from
/// one ffprobe of the file. The engine works on 8-bit RGB pictures of the first video stream. An
/// 8-bit or 10-bit 4:2:0 source is accepted: a 10-bit source is dithered into the engine's 8-bit
/// pictures and encoded back to 10-bit, which suits drawn content mastered in 8-bit and released in
/// 10-bit for compression. Anything else is refused before extraction: other chroma layouts or
/// depths, non-square pixels, rotation, or a second real video stream.
/// </summary>
public sealed record SuperResolutionSource(string? Refusal, double VideoStartSeconds, int AttachmentCount, int BitDepth = 8)
{
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
        var start = double.TryParse(Text(video, "start_time"), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? seconds
            : 0;
        var depth = Text(video, "pix_fmt") is { } format && Supported420.TryGetValue(format, out var bits) ? bits : 8;
        return new(RefusalFor(video, videos.Count), start, attachments, depth);
    }

    private static string? RefusalFor(JsonElement video, int videoStreams)
    {
        if (videoStreams > 1)
        {
            return "The file has more than one video stream; super-resolution upscales a single picture stream.";
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
