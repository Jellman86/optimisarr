using System.Diagnostics;
using System.Globalization;

namespace Optimisarr.Sidecar.Core.Session;

/// <summary>What FFmpeg said when it finished.</summary>
public sealed record TranscodeResult(int ExitCode, string ErrorTail, long? SizeBudgetExceededAtBytes = null)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>What a probe said on its standard output.</summary>
public sealed record ProbeResult(int ExitCode, string Output);

public sealed record OutputSizeBudget(string OutputPath, long MaxBytes);

/// <summary>Runs the encode the server asked for, and says how far through it is.</summary>
public interface ITranscoder
{
    Task<TranscodeResult> RunAsync(
        string ffmpeg,
        IReadOnlyList<string> arguments,
        IProgress<double>? encodedSeconds,
        CancellationToken cancellationToken,
        OutputSizeBudget? sizeBudget = null);

    /// <summary>
    /// Runs ffprobe and hands back what it printed.
    ///
    /// <para>Separate from <see cref="RunAsync"/> because that one appends FFmpeg's own progress
    /// options and keeps stderr; ffprobe would refuse the first and answers on stdout. Reusing it
    /// would have failed every probe and left the caller believing it could not read a timeline.</para>
    /// </summary>
    Task<ProbeResult> ProbeAsync(
        string ffprobe,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);
}

/// <summary>
/// FFmpeg as a child process.
///
/// <para>Arguments are passed as argv, never as a command line. A Windows path with spaces —
/// <c>C:\Optimisarr Work\The Dinosaurs S01E01.mkv</c> — is one argument and must stay one, and
/// building a string would turn it into several.</para>
/// </summary>
public sealed class ProcessTranscoder : ITranscoder
{
    private readonly Func<ProcessStartInfo, IChildProcess> _createProcess;
    private readonly TimeSpan? _drainGrace;

    public ProcessTranscoder() : this(info => new SystemChildProcess(info)) { }
    internal ProcessTranscoder(Func<ProcessStartInfo, IChildProcess> createProcess, TimeSpan? drainGrace = null)
    {
        _createProcess = createProcess;
        _drainGrace = drainGrace;
    }

    public async Task<TranscodeResult> RunAsync(
        string ffmpeg, IReadOnlyList<string> arguments, IProgress<double>? encodedSeconds,
        CancellationToken cancellationToken, OutputSizeBudget? sizeBudget = null)
    {
        var startInfo = StartInfo(ffmpeg, arguments);
        startInfo.ArgumentList.Add("-progress");
        startInfo.ArgumentList.Add("pipe:1");
        startInfo.ArgumentList.Add("-nostats");

        // Dispose the reporter after the child: an in-flight advisory callback cannot escape
        // the operation, even after a pipe inherited by another process has been closed.
        using var reporter = new DirectProgressObserver(encodedSeconds);
        await using var process = new OwnedChildProcess(_createProcess(startInfo), _drainGrace);
        if (!process.Start(cancellationToken)) return new(-1, $"Could not start {ffmpeg}.");
        var errorTail = process.Track(ReadErrorTailAsync(process.Error, process.ReadingToken));
        var progress = ReadProgressAsync(process.Output, reporter, process.ReadingToken);
        process.Track(progress);

        var exited = process.WaitForExitAsync(cancellationToken);
        while (sizeBudget is not null
            && await Task.WhenAny(exited, Task.Delay(TimeSpan.FromSeconds(2), cancellationToken)) != exited)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryOutputSize(sizeBudget.OutputPath) is not { } bytes || bytes <= sizeBudget.MaxBytes) continue;
            process.RequestStop();
            await exited;
            await process.DrainAsync();
            return new(-1, "Size saving budget exceeded.", bytes);
        }
        await exited;
        await process.DrainAsync();
        return new(process.ExitCode, CompletedText(errorTail).Trim());
    }

    public async Task<ProbeResult> ProbeAsync(
        string ffprobe, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        await using var process = new OwnedChildProcess(_createProcess(StartInfo(ffprobe, arguments)), _drainGrace);
        if (!process.Start(cancellationToken)) return new(-1, string.Empty);
        var output = process.Track(ChildText.ReadBoundedAsync(process.Output, process.ReadingToken));
        var error = process.Track(ChildText.ReadBoundedAsync(process.Error, process.ReadingToken));
        await process.WaitForExitAsync(cancellationToken);
        if (!await process.DrainAsync()) return new(-1, string.Empty);
        return new(process.ExitCode, CompletedText(output));
    }

    internal static ProcessStartInfo StartInfo(string executable, IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }

    internal static string CompletedText(Task<string> text) => text.IsCompletedSuccessfully ? text.Result : string.Empty;

    private static async Task ReadProgressAsync(
        TextReader output, DirectProgressObserver observer, CancellationToken cancellationToken)
    {
        await foreach (var line in ChildText.ReadLinesAsync(output, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!line.Truncated && line.Text.StartsWith("out_time_us=", StringComparison.Ordinal)
                && long.TryParse(line.Text.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out var micro)
                && micro > 0)
                observer.Report(micro / 1_000_000d);
        }
    }

    private static async Task<string> ReadErrorTailAsync(TextReader error, CancellationToken cancellationToken)
    {
        var tail = new Queue<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var line in ChildText.ReadLinesAsync(error, cancellationToken))
        {
            var text = line.Truncated ? line.Text + " [truncated]" : line.Text;
            if (!seen.Add(text)) continue;
            tail.Enqueue(text);
            while (tail.Count > 12) seen.Remove(tail.Dequeue());
        }
        return string.Join('\n', tail);
    }

    /// <summary>
    /// Observers are advisory and must return promptly. This fence covers synchronous Report
    /// calls; callbacks queued internally by an external IProgress implementation own their lifetime.
    /// </summary>
    private sealed class DirectProgressObserver(IProgress<double>? observer) : IDisposable
    {
        private readonly object _gate = new();
        private IProgress<double>? _observer = observer;
        public void Report(double seconds)
        {
            lock (_gate)
            {
                try { _observer?.Report(seconds); }
                catch (Exception) { _observer = null; }
            }
        }
        public void Dispose() { lock (_gate) _observer = null; }
    }

    private static long? TryOutputSize(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : null; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }
}
