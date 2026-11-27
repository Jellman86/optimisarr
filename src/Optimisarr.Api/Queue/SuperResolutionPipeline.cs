using System.Diagnostics;
using System.Globalization;
using System.Text;
using Optimisarr.Core.Queue;
using Optimisarr.Core.SuperResolution;

namespace Optimisarr.Api.Queue;

/// <summary>Everything one super-resolution attempt runs, fixed before the first process starts.</summary>
internal sealed record SuperResolutionRunRequest(
    string FfmpegPath,
    string EnginePath,
    IReadOnlyList<string> DecoderArguments,
    IReadOnlyList<string> EncoderArguments,
    Func<string, string, IReadOnlyList<string>> EngineArguments,
    string Workspace,
    PictureSize ExpectedUpscaled,
    int? ExpectedFrames,
    int FramesPerBatch,
    long WorstCaseBatchBytes,
    long ReserveBytes,
    TimeSpan StallLimit)
{
    /// <summary>The copy that adds the source's attachments once the encode succeeds.</summary>
    public SuperResolutionFinish? Finish { get; init; }
}

/// <summary>
/// A stream copy into the candidate that adds what the encode cannot carry without mistiming the
/// audio: the source's attachments. FFmpeg reads the whole source before it writes the candidate,
/// so the copy may run silent for <see cref="ReadAllowance"/> on top of the stall limit.
/// </summary>
internal sealed record SuperResolutionFinish(IReadOnlyList<string> Arguments, string CandidatePath, TimeSpan ReadAllowance)
{
    /// <summary>A pessimistic read rate for a busy pool, a USB disk or a network share.</summary>
    public const long SlowReadBytesPerSecond = 20L * 1024 * 1024;

    public static TimeSpan AllowanceFor(long sourceBytes) => TimeSpan.FromSeconds((double)Math.Max(0, sourceBytes) / SlowReadBytesPerSecond);
}

/// <summary>The queue's hooks: free space, pause state, process registration and progress.</summary>
internal sealed record SuperResolutionRunHooks(
    Func<long?> FreeBytes,
    Func<bool> IsPaused,
    Func<int, IDisposable?> TrackProcess,
    Func<double, Task> ReportProgress,
    Func<int, IDisposable?>? TrackEncoder = null,
    Func<string, bool>? TryReportEngineOutput = null,
    Action<int, int, int?>? ReportBatchCompleted = null);

/// <summary>
/// The super-resolution pipeline as a bounded stream. The decoder writes PNG pictures to a pipe
/// that is read one batch at a time; the engine upscales that batch directory to directory; the
/// upscaled pictures are written in order to a single encoder's stdin; then the batch is deleted.
/// Only one batch of source and upscaled pictures is ever on disk, whatever the video's length.
/// Every process is owned: it is killed and awaited before scratch is removed, its log is
/// bounded, and one pause-aware watchdog stops the attempt when nothing advances.
/// </summary>
internal static class SuperResolutionPipeline
{
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(1);

    public static async Task<QueueDispatcher.FfmpegRun> RunAsync(
        SuperResolutionRunRequest request, SuperResolutionRunHooks hooks, CancellationToken cancellationToken)
    {
        if (SuperResolutionScratch.PrepareAttempt(request.Workspace) is { } refusal)
            return new QueueDispatcher.FfmpegRun(-1, refusal, null);
        using var stalled = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stalled.Token);
        var token = linked.Token;
        var clock = new ProgressClock();
        var processes = new List<OwnedProcess>();
        string? stallReason = null;
        var watchdog = WatchAsync(clock, request.StallLimit, hooks.IsPaused, reason => { stallReason = reason; stalled.Cancel(); }, token);
        try
        {
            var decoder = Start(processes, request.FfmpegPath, request.DecoderArguments, hooks, readStdout: true, writeStdin: false);
            var encoder = Start(processes, request.FfmpegPath, request.EncoderArguments, hooks, readStdout: false, writeStdin: true, isEncoder: true);
            var written = 0;
            for (var batch = 0; ; batch++)
            {
                // Only another picture needs another batch's room: the end of the video must not be
                // refused for space a batch that will never be read would have used.
                if (await PngStreamReader.ReadNextAsync(decoder.Stdout!, token) is not { } first)
                {
                    break;
                }
                if (hooks.FreeBytes() is { } free && free - request.WorstCaseBatchBytes < request.ReserveBytes)
                {
                    return Failed($"Super-resolution stopped: the next batch needs up to {Gib(request.WorstCaseBatchBytes)} GiB " +
                        $"of working space above the {Gib(request.ReserveBytes)} GiB reserve, and only {Gib(free)} GiB is free.", processes);
                }

                var input = Path.Combine(request.Workspace, $"in-{batch:D6}");
                var output = Path.Combine(request.Workspace, $"out-{batch:D6}");
                Directory.CreateDirectory(input);
                Directory.CreateDirectory(output);
                await File.WriteAllBytesAsync(Path.Combine(input, FrameName(0)), first, token);
                var frames = 1;
                clock.Touch();
                while (frames < request.FramesPerBatch
                    && await PngStreamReader.ReadNextAsync(decoder.Stdout!, token) is { } picture)
                {
                    await File.WriteAllBytesAsync(Path.Combine(input, FrameName(frames)), picture, token);
                    frames++;
                    clock.Touch();
                }

                var engine = await RunEngineAsync(request, hooks, processes, input, output, written, reportDevice: batch == 0, clock, token);
                if (engine is not null)
                {
                    return engine;
                }

                for (var index = 0; index < frames; index++)
                {
                    var path = Path.Combine(output, FrameName(index));
                    if (!File.Exists(path))
                    {
                        return Failed($"The engine did not produce picture {written + index + 1}; reassembling would drop a frame.", processes);
                    }
                    var upscaled = await File.ReadAllBytesAsync(path, token);
                    var size = PngStreamReader.Dimensions(upscaled);
                    if (size != request.ExpectedUpscaled)
                    {
                        return Failed($"The engine produced a {size.Width}x{size.Height} picture where " +
                            $"{request.ExpectedUpscaled.Width}x{request.ExpectedUpscaled.Height} was expected.", processes);
                    }
                    await encoder.Stdin!.WriteAsync(upscaled, token);
                    clock.Touch();
                }
                written += frames;
                DeleteQuietly(input);
                DeleteQuietly(output);
                hooks.ReportBatchCompleted?.Invoke(batch + 1, written, request.ExpectedFrames);
                if (request.ExpectedFrames is > 0 and var expected)
                {
                    await hooks.ReportProgress(Math.Min(1.0, (double)written / expected));
                }
            }

            await encoder.Stdin!.FlushAsync(token);
            encoder.Stdin.Close();
            await decoder.WaitAsync(token);
            if (decoder.ExitCode != 0)
            {
                return Failed($"Frame decoding failed with exit code {decoder.ExitCode}.", processes);
            }
            await encoder.WaitAsync(token);
            if (encoder.ExitCode != 0)
            {
                return Failed($"Encoding the upscaled pictures failed with exit code {encoder.ExitCode}.", processes);
            }
            if (request.Finish is { } finish)
            {
                // Both have exited; disposing them now releases a hardware encoder's slot for the
                // copy, and their logs stay readable.
                await decoder.DisposeAsync();
                await encoder.DisposeAsync();
                var copy = Start(processes, request.FfmpegPath, finish.Arguments, hooks, readStdout: false, writeStdin: false);
                await WaitForCopyAsync(copy, finish, clock, token);
                if (copy.ExitCode != 0)
                {
                    TryDeleteFile(finish.CandidatePath);
                    return Failed($"Adding the source's attachments, such as subtitle fonts, failed with exit code {copy.ExitCode}.", processes);
                }
            }
            return new QueueDispatcher.FfmpegRun(0, null, Logs(processes));
        }
        catch (OperationCanceledException) when (stalled.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return Failed(stallReason ?? "Super-resolution stopped making progress.", processes);
        }
        catch (InvalidDataException error)
        {
            return Failed(error.Message, processes);
        }
        catch (IOException error) when (!token.IsCancellationRequested)
        {
            // A pipe closed under us: one of the processes ended early. Its own log says why.
            return Failed($"A super-resolution process ended unexpectedly: {error.Message}", processes);
        }
        finally
        {
            linked.Cancel();
            foreach (var process in processes)
            {
                await process.DisposeAsync();
            }
            try { await watchdog; } catch (OperationCanceledException) { }
            if (SuperResolutionScratch.MayRemoveAttempt(request.Workspace))
                DeleteQuietly(request.Workspace);
        }
    }

    internal static string FrameName(int index) => "f" + index.ToString("D8", CultureInfo.InvariantCulture) + ".png";

    private static async Task<QueueDispatcher.FfmpegRun?> RunEngineAsync(
        SuperResolutionRunRequest request, SuperResolutionRunHooks hooks, List<OwnedProcess> processes,
        string input, string output, int writtenBefore, bool reportDevice, ProgressClock clock, CancellationToken token)
    {
        var engine = Start(processes, request.EnginePath, request.EngineArguments(input, output), hooks, readStdout: false, writeStdin: false);
        var produced = 0;
        var deviceReported = !reportDevice || hooks.TryReportEngineOutput is null;
        while (!engine.HasExited)
        {
            await Task.WhenAny(engine.WaitAsync(token), Task.Delay(WatchdogInterval, token));
            token.ThrowIfCancellationRequested();
            if (!deviceReported)
            {
                deviceReported = hooks.TryReportEngineOutput!.Invoke(engine.Log);
            }
            var count = Directory.EnumerateFiles(output, "*.png").Count();
            if (count > produced)
            {
                produced = count;
                clock.Touch();
                // A batch of a slow model can run for over an hour, so the bar moves with each picture.
                if (request.ExpectedFrames is > 0 and var expected)
                {
                    await hooks.ReportProgress(Math.Min(1.0, (double)(writtenBefore + produced) / expected));
                }
            }
        }
        await engine.WaitAsync(token);
        processes.Remove(engine);
        await engine.DisposeAsync();
        if (!deviceReported)
        {
            hooks.TryReportEngineOutput!.Invoke(engine.Log);
        }
        return engine.ExitCode == 0 ? null
            : new QueueDispatcher.FfmpegRun(engine.ExitCode, $"The super-resolution engine exited with code {engine.ExitCode}.", engine.Log);
    }

    /// <summary>
    /// A copy has no pictures to count. It reads the whole source before writing, so it counts as
    /// progressing for its read allowance; after that, a growing candidate is its sign of progress.
    /// </summary>
    private static async Task WaitForCopyAsync(OwnedProcess process, SuperResolutionFinish finish, ProgressClock clock, CancellationToken token)
    {
        var started = Stopwatch.StartNew();
        var length = 0L;
        clock.Touch();
        while (!process.HasExited)
        {
            await Task.WhenAny(process.WaitAsync(token), Task.Delay(WatchdogInterval, token));
            token.ThrowIfCancellationRequested();
            var now = File.Exists(finish.CandidatePath) ? new FileInfo(finish.CandidatePath).Length : 0;
            if (now > length || started.Elapsed < finish.ReadAllowance)
            {
                length = Math.Max(length, now);
                clock.Touch();
            }
        }
        await process.WaitAsync(token);
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static OwnedProcess Start(List<OwnedProcess> processes, string path, IReadOnlyList<string> arguments,
        SuperResolutionRunHooks hooks, bool readStdout, bool writeStdin, bool isEncoder = false)
    {
        var track = isEncoder ? hooks.TrackEncoder ?? hooks.TrackProcess : hooks.TrackProcess;
        var process = OwnedProcess.Start(path, arguments, readStdout, writeStdin, track);
        processes.Add(process);
        return process;
    }

    private static async Task WatchAsync(ProgressClock clock, TimeSpan limit, Func<bool> isPaused, Action<string> stop, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await Task.Delay(WatchdogInterval, token);
            if (isPaused())
            {
                // A paused queue suspends these processes on purpose; silence is not a stall.
                clock.Touch();
            }
            else if (clock.Idle > limit)
            {
                stop($"Super-resolution made no progress for {limit.TotalMinutes:0.#} minutes and was stopped.");
                return;
            }
        }
    }

    private static QueueDispatcher.FfmpegRun Failed(string error, List<OwnedProcess> processes) => new(-1, error, Logs(processes));

    private static string Logs(List<OwnedProcess> processes) =>
        string.Join(Environment.NewLine, processes.Select(process => process.Log).Where(log => log.Length > 0));

    private static string Gib(long bytes) => (bytes / (1024.0 * 1024 * 1024)).ToString("0.#", CultureInfo.InvariantCulture);

    private static void DeleteQuietly(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Startup recovery clears the super-resolution scratch root before any new attempt.
        }
    }

    /// <summary>Monotonic time since anything last advanced.</summary>
    private sealed class ProgressClock
    {
        private long _last = Stopwatch.GetTimestamp();
        public void Touch() => Interlocked.Exchange(ref _last, Stopwatch.GetTimestamp());
        public TimeSpan Idle => Stopwatch.GetElapsedTime(Interlocked.Read(ref _last));
    }
}

/// <summary>
/// A child process this attempt owns: argument array, captured and bounded stderr, and disposal
/// that kills the whole tree and waits for it to exit, so scratch is never removed under it.
/// </summary>
internal sealed class OwnedProcess : IAsyncDisposable
{
    private const int MaximumLogCharacters = 32 * 1024;
    private readonly Process _process;
    private readonly IDisposable? _registration;
    private readonly StringBuilder _log = new();
    private readonly Task _stderr;

    private OwnedProcess(Process process, IDisposable? registration)
    {
        _process = process;
        _registration = registration;
        _stderr = Task.Run(async () =>
        {
            var buffer = new char[4096];
            int read;
            while ((read = await process.StandardError.ReadAsync(buffer)) > 0)
            {
                lock (_log)
                {
                    _log.Append(buffer, 0, read);
                    if (_log.Length > MaximumLogCharacters)
                    {
                        _log.Remove(0, _log.Length - MaximumLogCharacters);
                    }
                }
            }
        });
    }

    public Stream? Stdout { get; private init; }
    public Stream? Stdin { get; private init; }
    private int? _exitCode;
    public bool HasExited => _exitCode is not null || _process.HasExited;
    /// <summary>The exit code, still readable after disposal.</summary>
    public int ExitCode => _exitCode ?? _process.ExitCode;
    public string Log { get { lock (_log) return _log.ToString(); } }

    public static OwnedProcess Start(string path, IReadOnlyList<string> arguments, bool readStdout, bool writeStdin,
        Func<int, IDisposable?> track)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo(path)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = readStdout,
                RedirectStandardInput = writeStdin,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        process.Start();
        IDisposable? registration;
        try
        {
            registration = track(process.Id);
        }
        catch
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            process.Dispose();
            throw;
        }
        return new OwnedProcess(process, registration)
        {
            Stdout = readStdout ? process.StandardOutput.BaseStream : null,
            Stdin = writeStdin ? process.StandardInput.BaseStream : null,
        };
    }

    public async Task WaitAsync(CancellationToken token)
    {
        await _process.WaitForExitAsync(token);
        // Drain captured stderr before callers inspect Log on exit; WaitForExitAsync returns
        // before the background reader task finishes appending the final pipe buffer.
        try { await _stderr.WaitAsync(token); } catch (Exception error) when (error is IOException or ObjectDisposedException) { }
    }

    private bool _disposed;

    public async ValueTask DisposeAsync()
    {
        // A finished process may be disposed early, before the pipeline's final sweep.
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
        await _process.WaitForExitAsync(CancellationToken.None);
        _exitCode = _process.ExitCode;
        try { await _stderr; } catch (Exception error) when (error is IOException or ObjectDisposedException) { }
        _registration?.Dispose();
        _process.Dispose();
    }
}
