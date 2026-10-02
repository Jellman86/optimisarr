using System.Text.Json;
using Optimisarr.Core.Verification;

internal static class AudioStudyProgram
{
    public static async Task<int> Main(string[] args)
    {
        var options = AudioStudyOptions.Parse(args);
        if (options is null) { Console.Error.WriteLine(AudioStudyOptions.Usage); return 2; }
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += cancel;
        try { return await AudioStudyRunner.RunAsync(options, stop.Token); }
        finally { Console.CancelKeyPress -= cancel; }
    }
}

internal static class AudioStudyRunner
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal static async Task<int> RunAsync(AudioStudyOptions options, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            var reportPath = Path.GetFullPath(options.Report);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (new[] { options.Reference, options.Candidate, options.Ffmpeg, options.Ffprobe, options.Metric }
                .Any(p => Path.GetFullPath(p).Equals(reportPath, comparison)))
                throw new IOException("The report must have its own new path.");
            // Reserve before measurement so an existing report or input is never overwritten.
            await using var report = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(reportPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var started = DateTimeOffset.UtcNow;
            try
            {
                var result = await new AudioQualityService(options.Ffmpeg, options.Ffprobe, options.Metric, options.Scratch)
                    .MeasureAsync(options.Reference, options.Candidate, token);
                await JsonSerializer.SerializeAsync(report, new
                {
                    schema = 1, started, finished = DateTimeOffset.UtcNow, purpose = "audio-quality-qualification",
                    qualityGateEvaluated = false, replacementAuthorized = false, result
                }, Json, CancellationToken.None);
                Console.WriteLine(result.Measured
                    ? $"Audio assessed: {result.Windows.Count} windows, worst channel distance {result.WorstChannelDistance:0.######}. No quality gate evaluated."
                    : $"Audio assessment unavailable: {result.Error}");
                return result.Measured ? 0 : 1;
            }
            catch (OperationCanceledException)
            {
                await JsonSerializer.SerializeAsync(report, new
                {
                    schema = 1, started, finished = DateTimeOffset.UtcNow, purpose = "audio-quality-qualification",
                    qualityGateEvaluated = false, replacementAuthorized = false, measured = false, error = "Assessment cancelled."
                }, Json, CancellationToken.None);
                Console.Error.WriteLine("Assessment cancelled; owned scratch cleaned up.");
                return 130;
            }
        }
        catch (OperationCanceledException) { return 130; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { Console.Error.WriteLine(e.Message); return 1; }
    }
}
