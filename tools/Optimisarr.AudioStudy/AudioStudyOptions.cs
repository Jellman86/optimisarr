internal sealed record AudioStudyOptions(string Reference, string Candidate, string Ffmpeg, string Ffprobe, string Metric,
    string Report, string Scratch)
{
    internal const string Usage = "AudioStudy --reference source.wav --candidate encode.opus --ffmpeg /path/ffmpeg "
        + "--ffprobe /path/ffprobe --metric /path/optimisarr-audio-quality --report new-report.json [--scratch /path/scratch]";

    internal static AudioStudyOptions? Parse(string[] args)
    {
        var known = new HashSet<string> { "--reference", "--candidate", "--ffmpeg", "--ffprobe", "--metric", "--report", "--scratch" };
        var values = new Dictionary<string, string>();
        for (var i = 0; i < args.Length; i += 2)
        {
            if (!known.Contains(args[i]) || i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1])
                || args[i + 1].StartsWith("--", StringComparison.Ordinal) || !values.TryAdd(args[i], args[i + 1])) return null;
        }
        if (known.Where(k => k != "--scratch").Any(k => !values.ContainsKey(k))) return null;
        return new(values["--reference"], values["--candidate"], values["--ffmpeg"], values["--ffprobe"], values["--metric"],
            values["--report"], values.GetValueOrDefault("--scratch") ?? Path.GetTempPath());
    }
}
