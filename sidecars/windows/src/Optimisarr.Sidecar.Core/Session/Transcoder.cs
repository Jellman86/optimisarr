using System.Diagnostics;
using System.Globalization;

namespace Optimisarr.Sidecar.Core.Session;

/// <summary>What FFmpeg said when it finished.</summary>
public sealed record TranscodeResult(int ExitCode, string ErrorTail)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>Runs the encode the server asked for, and says how far through it is.</summary>
public interface ITranscoder
{
    Task<TranscodeResult> RunAsync(
        string ffmpeg,
        IReadOnlyList<string> arguments,
        IProgress<double>? encodedSeconds,
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
    public async Task<TranscodeResult> RunAsync(
        string ffmpeg,
        IReadOnlyList<string> arguments,
        IProgress<double>? encodedSeconds,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // Progress on stdout in a machine-readable form, so nothing has to be scraped out of the
        // human-facing stderr banner — which changes between FFmpeg versions.
        startInfo.ArgumentList.Add("-progress");
        startInfo.ArgumentList.Add("pipe:1");
        startInfo.ArgumentList.Add("-nostats");

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
        {
            return new TranscodeResult(-1, $"Could not start {ffmpeg}.");
        }

        var errorTail = ReadErrorTailAsync(process, cancellationToken);
        var progress = ReadProgressAsync(process, encodedSeconds, cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // A lapsed lease or a stopping service: the job is void either way, and leaving FFmpeg
            // running would hold the source open and keep the GPU busy for nothing.
            Kill(process);
            throw;
        }

        return new TranscodeResult(process.ExitCode, (await errorTail).Trim());
    }

    /// <summary>
    /// Follows FFmpeg's progress stream and reports encoded seconds as they pass.
    ///
    /// <para>Encoded seconds rather than a percentage: the server knows the source duration and
    /// scales it into the job's progress itself, so a worker that guessed a percentage would be
    /// second-guessing the only side that knows what the whole job is.</para>
    /// </summary>
    private static async Task ReadProgressAsync(
        Process process, IProgress<double>? encodedSeconds, CancellationToken cancellationToken)
    {
        if (encodedSeconds is null)
        {
            return;
        }

        try
        {
            while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
            {
                // out_time_us=123456789
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal)
                    && long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out var micro)
                    && micro > 0)
                {
                    encodedSeconds.Report(micro / 1_000_000d);
                }
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException)
        {
            // Progress is a convenience. Losing it must never fail an encode that is otherwise fine.
        }
    }

    /// <summary>
    /// Keeps the last few distinct stderr lines, for a one-line reason when the encode fails.
    ///
    /// <para>Distinct, because FFmpeg repeats a warning once per stream: a file with twenty audio
    /// tracks buries the one line that says what actually went wrong under twenty identical copies
    /// of something harmless.</para>
    /// </summary>
    private static async Task<string> ReadErrorTailAsync(Process process, CancellationToken cancellationToken)
    {
        var tail = new Queue<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            while (await process.StandardError.ReadLineAsync(cancellationToken) is { } line)
            {
                if (!seen.Add(line))
                {
                    continue;
                }

                tail.Enqueue(line);
                while (tail.Count > 12)
                {
                    seen.Remove(tail.Dequeue());
                }
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException)
        {
        }

        return string.Join('\n', tail);
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or SystemException)
        {
        }
    }
}
