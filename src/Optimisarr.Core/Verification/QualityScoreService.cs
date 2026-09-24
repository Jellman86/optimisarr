using System.Diagnostics;
using System.Text;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Tools;

namespace Optimisarr.Core.Verification;

/// <summary>The outcome of attempting a VMAF measurement: scores, or why it could not be measured.</summary>
public sealed record QualityResult(bool Measured, QualityScores? Scores, string? Error)
{
    public VmafAcceleration Acceleration { get; init; } = VmafAcceleration.None;

    public static QualityResult Ok(QualityScores scores) => new(true, scores, null);

    public static QualityResult Failed(string error) => new(false, null, error);
}

/// <summary>
/// Measures the perceptual quality of a converted output against its original with
/// FFmpeg's <c>libvmaf</c> filter, writing a JSON log that the pure
/// <see cref="QualityScoreParser"/> turns into scores. The distorted stream is
/// prepared by <see cref="QualityScoreCommandBuilder"/> so resolution, timebase,
/// colour range, model, and HDR-to-SDR handling are deterministic. FFmpeg is
/// invoked through an explicit argument list, never a shell string.
/// </summary>
public sealed class QualityScoreService(
    string? ffmpegCommand = null,
    string? cudaFfmpegCommand = null)
{
    private static readonly TimeSpan MeasurementProgressTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AlignmentProbeTimeout = TimeSpan.FromMinutes(2);
    // VMAF needs an ffmpeg built with libvmaf, which may differ from the transcoding
    // binary; the composition layer can point this at one (e.g. jellyfin-ffmpeg).
    private readonly string _ffmpeg = string.IsNullOrWhiteSpace(ffmpegCommand) ? "ffmpeg" : ffmpegCommand;
    private readonly string _cudaFfmpeg = string.IsNullOrWhiteSpace(cudaFfmpegCommand)
        ? string.IsNullOrWhiteSpace(ffmpegCommand) ? "ffmpeg" : ffmpegCommand
        : cudaFfmpegCommand;

    public async Task<QualityResult> MeasureAsync(
        string referencePath,
        string distortedPath,
        QualityMeasurementContext context,
        CancellationToken cancellationToken,
        IProgress<double>? progress = null)
    {
        if (!File.Exists(referencePath))
        {
            return QualityResult.Failed($"Original file does not exist: {referencePath}");
        }
        if (!File.Exists(distortedPath))
        {
            return QualityResult.Failed($"Output file does not exist: {distortedPath}");
        }

        string executableFor(VmafAcceleration acceleration) =>
            acceleration == VmafAcceleration.Cuda ? _cudaFfmpeg : _ffmpeg;

        // A unique log path with no special characters keeps the filtergraph valid.
        var logPath = Path.Combine(Path.GetTempPath(), $"optimisarr-vmaf-{Guid.NewGuid():N}.json");
        try
        {
            // Keep measurement useful without monopolising a small home server. Four
            // libvmaf workers scale well while leaving capacity for the API and disk I/O.
            var threads = Math.Clamp(Environment.ProcessorCount, 1, 4);
            var requestedAcceleration = context.ReferenceIsHdr
                ? VmafAcceleration.None
                : context.Acceleration;

            if (requestedAcceleration == VmafAcceleration.Cuda
                && !await HasFilterAsync(_cudaFfmpeg, "libvmaf_cuda", cancellationToken))
            {
                requestedAcceleration = VmafAcceleration.None;
            }

            var effectiveContext = context with { Acceleration = requestedAcceleration };

            // Measured rather than derived, and measured here because only this side holds both
            // files. The shift the builder would otherwise compute comes from the containers'
            // headers, which say the same thing for a pair that needs a correction and a pair that
            // is destroyed by one. A worker that supplied its own token has already done this.
            if (effectiveContext.DistortedShiftToken is null or { Length: 0 })
            {
                var chosen = await ChooseAlignmentAsync(
                    executableFor(requestedAcceleration), referencePath, distortedPath,
                    effectiveContext, threads, cancellationToken);
                if (chosen is not null)
                {
                    effectiveContext = effectiveContext with { DistortedShiftToken = chosen };
                }
            }

            var command = BuildCommand(
                distortedPath,
                referencePath,
                logPath,
                effectiveContext,
                threads);
            var executable = executableFor(requestedAcceleration);
            var result = await RunMeasurementAsync(
                executable,
                command,
                logPath,
                effectiveContext,
                cancellationToken,
                progress);

            if (result.Measured || requestedAcceleration == VmafAcceleration.None)
            {
                return result;
            }

            // Hardware decode is codec/profile/driver dependent, and CUDA VMAF also requires a
            // compatible GPU at runtime. Acceleration is only an optimisation: discard its log and
            // rerun the canonical software graph so a hardware limitation cannot fail verification.
            DeleteQuietly(logPath);
            var fallbackContext = context with { Acceleration = VmafAcceleration.None };
            var fallbackCommand = BuildCommand(
                distortedPath,
                referencePath,
                logPath,
                fallbackContext,
                threads);
            var fallback = await RunMeasurementAsync(
                _ffmpeg,
                fallbackCommand,
                logPath,
                fallbackContext,
                cancellationToken,
                progress);

            if (!fallback.Measured)
            {
                return QualityResult.Failed(
                    $"Accelerated VMAF failed ({result.Error}); software fallback failed: {fallback.Error}");
            }

            return fallback;
        }
        catch (ArgumentException ex)
        {
            return QualityResult.Failed($"Could not prepare VMAF measurement: {ex.Message}");
        }
        finally
        {
            DeleteQuietly(logPath);
        }
    }

    private static QualityScoreCommand BuildCommand(
        string distortedPath,
        string referencePath,
        string logPath,
        QualityMeasurementContext context,
        int threads) =>
        QualityScoreCommandBuilder.Build(
            distortedPath,
            referencePath,
            logPath,
            context,
            threads);

    private static async Task<QualityResult> RunMeasurementAsync(
        string executable,
        QualityScoreCommand command,
        string logPath,
        QualityMeasurementContext context,
        CancellationToken cancellationToken,
        IProgress<double>? progress)
    {
        string stderr;
        int exitCode;

        try
        {
            using var process = CreateProcess(executable, command.Arguments);
            process.Start();
            using var stalled = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            stalled.CancelAfter(MeasurementProgressTimeout);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(stalled.Token);
            // When clip-VMAF caps the measurement, progress is a fraction of the clip, not the file.
            var measuredSeconds = (double?)context.MeasureDurationSeconds ?? context.ReferenceDurationSeconds;
            var stderrTask = ReadStderrWithProgressAsync(
                process.StandardError,
                measuredSeconds,
                progress,
                stalled.Token,
                () => stalled.CancelAfter(MeasurementProgressTimeout));

            try
            {
                await process.WaitForExitAsync(stalled.Token);
                await stdoutTask;
                stderr = await stderrTask;
            }
            catch (OperationCanceledException)
            {
                KillQuietly(process);
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                return QualityResult.Failed(
                    $"FFmpeg made no VMAF progress for {MeasurementProgressTimeout.TotalMinutes:0} minutes; measurement stopped to protect the host.");
            }

            exitCode = process.ExitCode;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return QualityResult.Failed($"Could not run ffmpeg libvmaf: {ex.Message}");
        }

        if (exitCode != 0)
        {
            var message = FirstLine(stderr) ?? $"ffmpeg libvmaf exited with code {exitCode}";
            return QualityResult.Failed(message);
        }

        if (!File.Exists(logPath))
        {
            return QualityResult.Failed(
                "libvmaf produced no log because no comparable video frames were available.");
        }

        var json = await File.ReadAllTextAsync(logPath, cancellationToken);
        var scores = QualityScoreParser.Parse(json);
        if (scores is not null)
        {
            scores = scores with
            {
                ModelVersion = command.ModelVersion,
                Preprocessing = command.Preprocessing
            };
        }

        return scores is null
            ? QualityResult.Failed("libvmaf log contained no comparable video frames or usable VMAF score.")
            : QualityResult.Ok(scores) with { Acceleration = context.Acceleration };
    }

    /// <summary>
    /// Runs the measurement itself, cut short, at each offset and returns the one that matched
    /// best, or null when none could be scored — in which case the builder falls back to what the
    /// containers claim, which is what it always did.
    /// </summary>
    private static async Task<string?> ChooseAlignmentAsync(
        string executable,
        string referencePath,
        string distortedPath,
        QualityMeasurementContext context,
        int threads,
        CancellationToken cancellationToken)
    {
        if (TimelineAlignmentProbe.FrameSeconds(context.ReferenceFrameRate) is not { } frameSeconds)
        {
            return null;
        }

        var scored = new List<(int Frames, double Mean)>(TimelineAlignmentProbe.FramesToTry.Count);
        foreach (var frames in TimelineAlignmentProbe.FramesToTry)
        {
            var log = Path.Combine(Path.GetTempPath(), $"optimisarr-align-{Guid.NewGuid():N}.json");
            try
            {
                var measurement = BuildCommand(
                    distortedPath,
                    referencePath,
                    log,
                    context with { DistortedShiftToken = TimelineAlignmentProbe.Format(frames * frameSeconds) },
                    threads);
                using var probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                probeTimeout.CancelAfter(AlignmentProbeTimeout);
                using var process = CreateProcess(
                    executable,
                    TimelineAlignmentProbe.Truncate(measurement.Arguments, TimelineAlignmentProbe.ProbeSeconds));
                process.Start();
                var stderr = process.StandardError.ReadToEndAsync(probeTimeout.Token);
                try
                {
                    await process.WaitForExitAsync(probeTimeout.Token);
                    await stderr;
                }
                catch (OperationCanceledException)
                {
                    KillQuietly(process);
                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    return null;
                }

                if (process.ExitCode != 0 || !File.Exists(log))
                {
                    continue;
                }

                if (TimelineAlignmentProbe.MeanScore(await File.ReadAllTextAsync(log, cancellationToken)) is { } mean)
                {
                    scored.Add((frames, mean));
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                // An alignment that cannot be probed is not a verification that cannot be run.
                return null;
            }
            finally
            {
                DeleteQuietly(log);
            }
        }

        return TimelineAlignmentProbe.Choose(scored) is { } chosen
            ? TimelineAlignmentProbe.Format(chosen * frameSeconds)
            : null;
    }

    private static async Task<bool> HasFilterAsync(
        string executable,
        string filter,
        CancellationToken cancellationToken)
    {
        try
        {
            using var filterTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            filterTimeout.CancelAfter(AlignmentProbeTimeout);
            using var process = CreateProcess(executable, ["-hide_banner", "-filters"]);
            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync(filterTimeout.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(filterTimeout.Token);
            try
            {
                await process.WaitForExitAsync(filterTimeout.Token);
                var stdout = await stdoutTask;
                await stderrTask;
                return process.ExitCode == 0 && FfmpegFilterParser.Contains(stdout, filter);
            }
            catch (OperationCanceledException)
            {
                KillQuietly(process);
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                return false;
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private static Process CreateProcess(string executable, IReadOnlyList<string> arguments)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        return process;
    }

    // libvmaf prints per-frame "time=" stats to stderr (enabled with -stats). Translate that into a
    // 0..1 fraction of the reference runtime so the queue can show real verification progress, while
    // still collecting the non-progress lines so a failure keeps its readable ffmpeg reason.
    private static async Task<string> ReadStderrWithProgressAsync(
        StreamReader reader,
        double? durationSeconds,
        IProgress<double>? progress,
        CancellationToken cancellationToken,
        Action onProgress)
    {
        var builder = new StringBuilder();
        var lastReported = 0.0;
        var lastElapsed = -1.0;

        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            var sample = FfmpegProgressParser.Parse(line);
            if (sample.ElapsedSeconds is { } elapsed)
            {
                if (elapsed > lastElapsed)
                {
                    lastElapsed = elapsed;
                    onProgress();
                }
                if (progress is not null && durationSeconds is > 0)
                {
                    var fraction = Math.Clamp(elapsed / durationSeconds.Value, 0, 0.999);
                    if (fraction - lastReported >= 0.01)
                    {
                        lastReported = fraction;
                        progress.Report(fraction);
                    }
                }

                // Progress frames are the bulk of stderr and are not part of a failure reason.
                continue;
            }

            builder.AppendLine(line);
        }

        return builder.ToString();
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // Best effort; the process is exiting anyway.
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A leftover temp log is harmless.
        }
    }

    private static string? FirstLine(string value) =>
        value.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
}
