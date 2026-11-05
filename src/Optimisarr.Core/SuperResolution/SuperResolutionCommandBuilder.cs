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
    public static IReadOnlyList<string> BuildDecoder(string inputPath, int threads, int sourceBitDepth, SuperResolutionColour colour)
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
        args.AddRange(["-vf", $"scale=in_color_matrix={colour.Matrix}:in_range={colour.Range}{dither},format=rgb24"]);
        args.AddRange(["-pix_fmt", "rgb24", "-c:v", "png", "-f", "image2pipe", "-"]);
        return args;
    }

    /// <summary>
    /// Encodes the upscaled pictures arriving on stdin, with the original as the second input so
    /// audio, subtitles, chapters, metadata and (for Matroska) attachments such as subtitle fonts
    /// are copied exactly as the source carries them, minus the kept-language removals the spec
    /// resolved. The pictures are placed at the source video's start so audio stays in sync.
    /// </summary>
    public static IReadOnlyList<string> BuildEncoder(
        TranscodeSpec spec,
        string resolvedEncoder,
        double frameRate,
        int threads,
        string? optimisedMarker,
        double videoStartSeconds,
        int sourceBitDepth,
        SuperResolutionColour colour)
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
        if (spec.Crf is { } crf)
        {
            // Super-resolution runs on the CPU encoder families only (a hardware family needs its
            // upload filters, a combination this pipeline refuses), so CRF is their quality knob.
            args.AddRange(["-crf", crf.ToString(CultureInfo.InvariantCulture)]);
        }
        // The pictures arrive as RGB: convert back with the source's matrix and range to its own
        // 4:2:0 layout and bit depth, and restore the colour tags it carried.
        var layout = sourceBitDepth > 8 ? "yuv420p10le" : "yuv420p";
        args.AddRange(["-vf", $"scale=out_color_matrix={colour.Matrix}:out_range={colour.Range},format={layout}"]);
        if (colour.Tagged)
        {
            if (colour.Space is { } space) args.AddRange(["-colorspace", space]);
            if (colour.Primaries is { } primaries) args.AddRange(["-color_primaries", primaries]);
            if (colour.Transfer is { } transfer) args.AddRange(["-color_trc", transfer]);
        }
        if (colour.RangeTagged) args.AddRange(["-color_range", colour.Range]);
        if (!string.IsNullOrWhiteSpace(spec.Preset))
        {
            args.AddRange(["-preset", spec.Preset]);
        }

        if (copiesSource)
        {
            // A video may carry no audio, subtitles or attachments; optional maps keep the
            // candidate buildable either way.
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
            // A library's audio re-encode rules do not apply to a super-resolution job.
            args.AddRange(["-c:a", "copy", "-c:s", IsMp4Family(spec.OutputPath) ? "mov_text" : "copy"]);
            if (!IsMp4Family(spec.OutputPath))
            {
                // Fonts and other attachments are part of how Matroska subtitles render.
                args.AddRange(["-map", "1:t?", "-c:t", "copy"]);
            }
        }

        AppendMarker(args, spec.OutputPath, optimisedMarker);
        args.Add(spec.OutputPath);
        return args;
    }

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
