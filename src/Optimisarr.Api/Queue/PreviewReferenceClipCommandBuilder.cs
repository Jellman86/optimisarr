namespace Optimisarr.Api.Queue;

/// <summary>
/// Builds the ffmpeg command arguments for the original-side reference segment used by clipped
/// previews. The verifier compares the encoded sample against this same window, not the full file.
/// </summary>
internal static class PreviewReferenceClipCommandBuilder
{
    public static IReadOnlyList<string> Build(string inputPath, string outputPath, int clipSeconds, int? clipStartSeconds)
    {
        var args = new List<string> { "-y" };

        if (clipStartSeconds is { } start and > 0)
        {
            args.Add("-ss");
            args.Add(start.ToString());
        }

        args.Add("-i");
        args.Add(inputPath);
        args.Add("-t");
        args.Add(clipSeconds.ToString());
        args.Add("-map");
        args.Add("0");
        args.Add("-c");
        args.Add("copy");
        args.Add("-avoid_negative_ts");
        args.Add("make_zero");
        args.Add(outputPath);

        return args;
    }

    public static IReadOnlyList<string> BuildAudio(
        string inputPath,
        string outputPath,
        int clipSeconds,
        int? clipStartSeconds)
    {
        var args = new List<string> { "-y" };
        if (clipStartSeconds is { } start and > 0)
        {
            args.Add("-ss");
            args.Add(start.ToString());
        }

        args.Add("-i");
        args.Add(inputPath);
        args.Add("-t");
        args.Add(clipSeconds.ToString());
        args.Add("-map");
        args.Add("0:a:0");
        args.Add("-c:a");
        args.Add("flac");
        args.Add(outputPath);
        return args;
    }
}
