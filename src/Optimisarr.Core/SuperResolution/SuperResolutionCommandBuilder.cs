using System.Globalization;
using Optimisarr.Core.Queue;

namespace Optimisarr.Core.SuperResolution;

/// <summary>
/// Builds the engine-neutral command stages of a super-resolution video job: decoding the source's
/// pictures as a PNG stream and reassembling the upscaled pictures with the source's audio,
/// subtitles and attachments. The engine's own invocation comes from <see cref="ISuperResolutionEngine"/>. Every command is a flat
/// argument array — never a shell string — per the repository's safety standard. Pure and unit
/// tested; it runs nothing.
/// </summary>
public static class SuperResolutionCommandBuilder
{
    /// <summary>The frame file name pattern for a batch directory handed to the engine.</summary>
    public const string FramePattern = "f%08d.png";

    /// <summary>
    /// Decodes the first video stream to a continuous stream of RGB PNG pictures on stdout. The
    /// pipeline reads it one picture at a time, so the decoder blocks while a batch is with the
    /// engine and scratch space never grows with the length of the video. The engine owns the
    /// picture work; the only filter is the dither a deeper source needs.
    /// </summary>
    public static IReadOnlyList<string> BuildDecoder(string inputPath, int threads, int sourceBitDepth, SuperResolutionColour colour, bool deinterlace = false,
        Queue.NoiseReduction denoise = Queue.NoiseReduction.Off)
    {
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-loglevel", "error",
            "-threads", threads.ToString(CultureInfo.InvariantCulture),
            "-i", inputPath,
            "-map", "0:v:0",
            "-fps_mode", "passthrough",
        };
        // Convert to RGB with the source's own matrix and range, which the encoder reverses. The
        // engine reads 8-bit pictures; error diffusion carries a deeper source's gradients into
        // them without the banding a plain truncation would add.
        var dither = sourceBitDepth > 8 ? ":sws_dither=ed" : "";
        // Fields are combined, then noise reduced, before the engine sees the picture: an upscaler sharpens
        // noise into detail it then cannot lose.
        var deinterlaceFirst = (deinterlace ? $"{Domain.VideoFieldOrder.DeinterlaceFilter}," : "")
            + (Queue.NoiseReductionFilter.For(denoise) is { } noise ? $"{noise}," : "");
        args.AddRange(["-vf", $"{deinterlaceFirst}scale=in_color_matrix={colour.Matrix}:in_range={colour.Range}{dither},format=rgb24"]);
        args.AddRange(["-pix_fmt", "rgb24", "-c:v", "png", "-f", "image2pipe", "-"]);
        return args;
    }

    /// <summary>
    /// Encodes the upscaled pictures arriving on stdin, with the original as the second input so
    /// audio, subtitles, chapters and metadata are copied exactly as the source carries them, minus
    /// the kept-language removals the spec resolved. For Matroska attachments the encode writes to
    /// <paramref name="encodeTo"/>, and <see cref="BuildAttachmentRemux"/> then adds them. The pictures are placed at the source video's start so audio stays in sync. The
    /// engine writes pictures with no pixel shape, so a non-square source's shape is set again here;
    /// without it a widescreen DVD would play squashed.
    /// </summary>
    public static IReadOnlyList<string> BuildEncoder(
        TranscodeSpec spec,
        string resolvedEncoder,
        double frameRate,
        int threads,
        string? optimisedMarker,
        double videoStartSeconds,
        int sourceBitDepth,
        SuperResolutionColour colour,
        Domain.SampleAspectRatio? pixelAspect = null,
        string? encodeTo = null)
    {
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-y",
            "-threads", threads.ToString(CultureInfo.InvariantCulture),
        };
        if (videoStartSeconds != 0)
        {
            args.AddRange(["-itsoffset", videoStartSeconds.ToString("R", CultureInfo.InvariantCulture)]);
        }
        args.AddRange(["-f", "image2pipe", "-framerate", frameRate.ToString("R", CultureInfo.InvariantCulture), "-c:v", "png", "-i", "-"]);

        var copiesSource = !spec.VideoOnly;
        if (copiesSource)
        {
            args.AddRange(["-i", spec.InputPath]);
            // The pictures carry no global metadata or chapters; the source does.
            args.AddRange(["-map_metadata", "1", "-map_chapters", "1"]);
        }

        args.AddRange(["-map", "0:v:0", "-c:v:0", resolvedEncoder]);
        FfmpegCommandBuilder.AppendQualityArguments(args, resolvedEncoder, spec.Crf);
        // The pictures arrive as RGB: convert back with the source's matrix and range to its own
        // 4:2:0 layout and bit depth, and restore the colour tags it carried.
        var nvenc = resolvedEncoder.EndsWith("_nvenc", StringComparison.OrdinalIgnoreCase);
        // NVENC accepts software frames and performs its own upload. Its 10-bit input is
        // semiplanar P010, unlike the planar 10-bit input used by the software encoders.
        var layout = sourceBitDepth > 8 ? nvenc ? "p010le" : "yuv420p10le" : "yuv420p";
        var shape = pixelAspect is { IsSquare: false } aspect ? $",setsar=sar={aspect.FilterValue}" : "";
        args.AddRange(["-vf", $"scale=out_color_matrix={colour.Matrix}:out_range={colour.Range}{shape},format={layout}"]);
        if (colour.Tagged)
        {
            if (colour.Space is { } space) args.AddRange(["-colorspace", space]);
            if (colour.Primaries is { } primaries) args.AddRange(["-color_primaries", primaries]);
            if (colour.Transfer is { } transfer) args.AddRange(["-color_trc", transfer]);
        }
        if (colour.RangeTagged) args.AddRange(["-color_range", colour.Range]);
        if (resolvedEncoder == "h264_nvenc" && colour.RangeTagged)
        {
            // NVENC can omit H.264's limited-range VUI when the other colour tags are unknown.
            args.AddRange(["-bsf:v:0", "h264_metadata=video_full_range_flag=" + (colour.Range == "pc" ? "1" : "0")]);
        }
        if (!string.IsNullOrWhiteSpace(spec.Preset))
        {
            args.AddRange(["-preset", spec.Preset]);
        }
        if (spec.Tuning is { IsEmpty: false } tuning)
        {
            args.AddRange(EncoderTuningPolicy.Resolve(resolvedEncoder, tuning));
        }

        if (copiesSource)
        {
            // A video may carry no audio or subtitles; optional maps keep the candidate buildable
            // either way.
            args.AddRange(["-map", "1:a?"]);
            foreach (var index in spec.RemoveAudioStreamIndexes ?? [])
            {
                args.AddRange(["-map", $"-1:a:{index}"]);
            }
            args.AddRange(["-map", "1:s?"]);
            foreach (var index in spec.RemoveSubtitleStreamIndexes ?? [])
            {
                args.AddRange(["-map", $"-1:s:{index}"]);
            }
            // Audio and subtitles are copied verbatim: super-resolution replaces the picture only.
            // A library's audio re-encode rules do not apply to a super-resolution job. Attachments
            // are not copied here: with one beside the piped pictures, FFmpeg 7 stops pacing the
            // source's audio against them and writes nearly all of it before the first picture.
            // BuildAttachmentRemux adds them once the encode is done.
            args.AddRange(["-c:a", "copy", "-c:s", IsMp4Family(spec.OutputPath) ? "mov_text" : "copy"]);
        }

        AppendMarker(args, spec.OutputPath, optimisedMarker);
        args.Add(encodeTo ?? spec.OutputPath);
        return args;
    }

    /// <summary>Where the encode writes when attachments are added afterwards: the attempt's own
    /// scratch, which is removed with it, in the candidate's container.</summary>
    public static string VideoPassPath(string workspace, string outputPath) =>
        Path.Combine(workspace, "video" + Path.GetExtension(outputPath).ToLowerInvariant());

    /// <summary>
    /// Copies the finished video pass into the candidate with the source's attachments, such as
    /// subtitle fonts, which are part of how Matroska subtitles render. Both inputs are files read
    /// at disk speed, so the streams interleave as they should.
    /// </summary>
    public static IReadOnlyList<string> BuildAttachmentRemux(string videoPassPath, string sourcePath, string outputPath) =>
    [
        "-hide_banner", "-nostdin", "-y",
        "-i", videoPassPath, "-i", sourcePath,
        "-map", "0", "-map", "1:t?", "-map_metadata", "0", "-map_chapters", "0",
        "-c", "copy", outputPath,
    ];

    private static void AppendMarker(List<string> args, string outputPath, string? optimisedMarker)
    {
        if (string.IsNullOrWhiteSpace(optimisedMarker))
        {
            return;
        }
        args.Add("-metadata");
        args.Add($"optimisarr={optimisedMarker}");
        if (IsMp4Family(outputPath))
        {
            args.Add("-movflags");
            args.Add("use_metadata_tags");
        }
    }

    private static bool IsMp4Family(string outputPath) =>
        Path.GetExtension(outputPath).ToLowerInvariant() is ".mp4" or ".m4v" or ".mov" or ".m4a" or ".m4b";
}
