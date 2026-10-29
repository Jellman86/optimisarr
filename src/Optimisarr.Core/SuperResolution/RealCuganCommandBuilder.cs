using System.Globalization;
using Optimisarr.Core.Queue;

namespace Optimisarr.Core.SuperResolution;

/// <summary>
/// Builds the three command stages of a Real-CUGAN video job: extracting the source's frames
/// as a PNG sequence, running the engine directory-to-directory over them, and reassembling the
/// upscaled frames with the source's audio, subtitles and attachments. Every command is a flat
/// argument array — never a shell string — per the repository's safety standard. Pure and unit
/// tested; it runs nothing.
/// </summary>
public static class RealCuganCommandBuilder
{
    /// <summary>The frame file name pattern both the extraction and the reassembly agree on.</summary>
    public const string FramePattern = "f%08d.png";

    /// <summary>
    /// The engine invocation. Only flags the realcugan-ncnn-vulkan CLI actually documents are
    /// passed: input and output directories, the model directory selected by the preset, the
    /// scale, the noise level, the device, and the PNG output format. Tuning knobs the operator
    /// has not configured (tile size, sync gap, thread ratios, TTA) stay at the engine defaults.
    /// </summary>
    public static IReadOnlyList<string> BuildEngineArguments(
        string inputDirectory,
        string outputDirectory,
        string modelsRoot,
        SuperResolutionOptions options)
    {
        var modelDirectory = Path.Combine(modelsRoot, RealCuganModelCatalog.ModelDirectoryFor(options.Preset)!);
        var args = new List<string>
        {
            "-i", inputDirectory,
            "-o", outputDirectory,
            "-m", modelDirectory,
            "-s", options.Scale.ToString(CultureInfo.InvariantCulture),
            "-n", options.NoiseLevel.ToString(CultureInfo.InvariantCulture),
        };
        if (options.Device == "cpu")
        {
            // The engine's documented CPU fallback: gpu id -1.
            args.AddRange(["-g", "-1"]);
        }
        else if (options.GpuIndex is { } gpu)
        {
            args.AddRange(["-g", gpu.ToString(CultureInfo.InvariantCulture)]);
        }
        // "auto" passes nothing: the engine's own default picks the Vulkan device.

        args.AddRange(["-f", "png"]);
        return args;
    }

    /// <summary>
    /// Extracts every source frame as a zero-based PNG sequence. Frames pass through
    /// untouched — the super-resolution engine does the scaling, so no filters run here.
    /// </summary>
    public static IReadOnlyList<string> BuildFrameExtraction(
        string inputPath, string framesDirectory, double frameRate, int threads)
    {
        var args = new List<string>
        {
            "-threads", threads.ToString(CultureInfo.InvariantCulture),
            "-i", inputPath,
        };
        AppendFrameExtractionFlags(args);
        args.Add(JoinFramePath(framesDirectory));
        return args;
    }

    /// <summary>
    /// Reassembles the upscaled frames with the source's kept streams. The frame sequence is
    /// the video; the original file rides along as the second input so audio, subtitles and
    /// (for Matroska) attachments are stream-copied exactly as the source carries them, with
    /// the kept-languages removals the spec already resolved. HDR tone mapping, crops and
    /// downscales do not apply: an HDR source is refused before this stage, and the engine
    /// owns the picture geometry.
    /// </summary>
    public static IReadOnlyList<string> BuildReassembly(
        TranscodeSpec spec,
        string resolvedEncoder,
        string framesDirectory,
        double frameRate,
        int threads,
        string? optimisedMarker)
    {
        var args = new List<string> { "-threads", threads.ToString(CultureInfo.InvariantCulture) };
        AppendSequenceInput(args, framesDirectory, frameRate);

        // The source input is only needed when something else is copied from it.
        var copiesSource = !spec.VideoOnly;
        if (copiesSource)
        {
            args.Add("-i");
            args.Add(spec.InputPath);

            // The frames carry no global metadata or chapters; the source does. Point both at
            // the source so the candidate keeps them, the way the normal path gets them for free
            // from its single input.
            args.AddRange(["-map_metadata", "1"]);
            args.AddRange(["-map_chapters", "1"]);

            // Mapping is a whitelist — the frames' video, the source's audio and subtitles, its
            // metadata and chapters. Attachments and data streams are simply never mapped.
        }

        args.Add("-map");
        args.Add("0:v:0");
        args.Add("-c:v:0");
        args.Add(resolvedEncoder);

        if (spec.Crf is { } crf)
        {
            // The reassembly encodes with the same encoder the normal path resolved, so the
            // quality knob follows that encoder family's vocabulary. Super-resolution runs on
            // the CPU encoder families only (a hardware family needs its upload filters, which
            // is an untested combination this pipeline refuses — see SuperResolutionRefusals).
            args.AddRange(["-crf", crf.ToString(CultureInfo.InvariantCulture)]);
        }
        // The frames arrive as RGB PNGs; without this the encoder would happily produce an
        // RGB/4:4:4 stream most delivery players cannot handle. The source's own 8-bit YUV
        // planar layout is what a super-resolution job is judged against, so that is the target.
        args.Add("-pix_fmt");
        args.Add("yuv420p");
        if (!string.IsNullOrWhiteSpace(spec.Preset))
        {
            args.Add("-preset");
            args.Add(spec.Preset);
        }

        if (!copiesSource)
        {
            AppendMarker(args, spec.OutputPath, optimisedMarker);
            args.Add(spec.OutputPath);
            return args;
        }

        // A video may carry no audio or no subtitles at all; the optional map keeps the
        // candidate buildable either way, and the codec options below then apply to nothing.
        args.Add("-map");
        args.Add("1:a?");
        foreach (var index in spec.RemoveAudioStreamIndexes ?? [])
        {
            args.Add("-map");
            args.Add($"-1:a:{index}");
        }
        args.Add("-map");
        args.Add("1:s?");
        foreach (var index in spec.RemoveSubtitleStreamIndexes ?? [])
        {
            args.Add("-map");
            args.Add($"-1:s:{index}");
        }

        // Audio and subtitles are copied verbatim: super-resolution replaces the picture only.
        // A library's audio re-encode rules do not apply to a super-resolution job (documented
        // limitation) — the spec's audio settings describe the normal path's encoder, and applying
        // them here without the normal path's audio planning would be a silent behaviour change.
        args.Add("-c:a");
        args.Add("copy");
        args.Add("-c:s");
        args.Add(IsMp4Family(spec.OutputPath) ? "mov_text" : "copy");

        AppendMarker(args, spec.OutputPath, optimisedMarker);
        args.Add(spec.OutputPath);
        return args;
    }

    private static void AppendSequenceInput(List<string> args, string framesDirectory, double frameRate)
    {
        args.AddRange(["-f", "image2"]);
        args.AddRange(["-framerate", frameRate.ToString("0.###", CultureInfo.InvariantCulture)]);
        args.AddRange(["-start_number", "0"]);
        args.Add("-i");
        args.Add(JoinFramePath(framesDirectory));
    }

    private static string JoinFramePath(string framesDirectory) =>
        framesDirectory.EndsWith('/') || framesDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? $"{framesDirectory}{FramePattern}"
            : $"{framesDirectory}/{FramePattern}";

    private static void AppendFrameExtractionFlags(List<string> args)
    {
        // Every frame, in source order and source timing; the sequence numbers from zero so the
        // reassembly reads back exactly what was written.
        args.AddRange(["-fps_mode", "passthrough"]);
        args.AddRange(["-start_number", "0"]);
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
