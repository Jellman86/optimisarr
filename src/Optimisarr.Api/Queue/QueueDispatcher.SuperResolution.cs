using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Library;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Queue;
using Optimisarr.Core.SuperResolution;
using Optimisarr.Data;

namespace Optimisarr.Api.Queue;

/// <summary>
/// The Real-CUGAN super-resolution pipeline: a video job's frames are extracted as a PNG
/// sequence, upscaled by the engine directory-to-directory, and reassembled with the source's
/// audio, subtitles and attachments into the candidate the normal verification and replacement
/// flow then judges. It reuses <see cref="QueueDispatcher"/>'s process handling, progress
/// reporting, work-directory reservation and cleanup — a super-resolution job is the same job,
/// with a three-stage encode.
/// </summary>
public sealed partial class QueueDispatcher
{
    // Share of the overall progress each stage owns. Extraction and reassembly are cheap I/O
    // and re-encode work; the engine pass is where essentially all the time goes.
    internal const double ExtractionProgressShare = 0.15;
    internal const double EngineProgressShare = 0.70;
    internal const double ReassemblyProgressShare = 0.15;

    /// <summary>The engine writes nothing for long stretches between large tiles of a frame;
    /// ten silent minutes means it is never going to finish this frame.</summary>
    internal static readonly TimeSpan EngineStallLimit = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan EngineProgressPollInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The engine configuration this attempt would run under, or null when this attempt keeps
    /// its normal path: the library opted in, the global engine is enabled, and the job is an
    /// ordinary replace-bound video encode — previews, calibration candidates and non-video
    /// jobs keep their normal path unchanged. Returning the options (rather than a bare flag)
    /// lets the caller carry the engine's scale into the expectations verification holds the
    /// candidate to.
    /// </summary>
    private async Task<SuperResolutionOptions?> ResolveSuperResolutionAsync(
        JobWork work, CancellationToken cancellationToken)
    {
        // The shape gate is pure, so a preview or an audio job never reads settings at all. The
        // Enabled check below repeats RoutesThroughSuperResolution for the same reason — it skips
        // the library query when the engine is off. The pure function stays the authority; these
        // early returns exist only to avoid the I/O it would otherwise answer anyway.
        if (!IsSuperResolutionCandidate(work))
        {
            return null;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<SettingsStore>();
        var options = await settings.GetSuperResolutionOptionsAsync(cancellationToken);
        if (!options.Enabled)
        {
            return null;
        }

        // The library decides per library, so an opted-in library can never change how another
        // library's jobs run. The candidate's media file identifies the library.
        var scopedDb = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var library = await scopedDb.Libraries
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.MediaFiles.Any(file => file.Id == work.MediaFileId),
                cancellationToken);
        return RoutesThroughSuperResolution(work, options, library?.SuperResolutionEnabled == true)
            ? options
            : null;
    }

    /// <summary>
    /// The job-side shape that may run through the engine at all: an ordinary replace-bound video
    /// encode. A preview or calibration sample exists to show or compare a normal encode — routing
    /// it through super-resolution would change what it previews — and non-video jobs and clips
    /// have no frames for the engine to upscale.
    /// </summary>
    internal static bool IsSuperResolutionCandidate(JobWork work) =>
        work.Spec.Kind == MediaKind.Video
        && work.Spec.VideoCodec is not null
        && !work.IsDisposable
        && work.Spec.ClipSeconds is null
        && !work.Spec.VideoOnly;

    /// <summary>
    /// The whole routing decision as pure facts: the job's shape, the engine's switch, and whether
    /// this library opted in. "The engine off changes nothing" is one line here, which is what
    /// lets a test hold it: an opted-in library with the engine disabled routes nowhere, so its
    /// jobs take exactly the path they always took.
    /// </summary>
    internal static bool RoutesThroughSuperResolution(
        JobWork work, SuperResolutionOptions options, bool libraryOptedIn) =>
        IsSuperResolutionCandidate(work) && options.Enabled && libraryOptedIn;

    /// <summary>
    /// The placement a job effectively runs under. A library opted into super-resolution runs
    /// its video jobs on this server only: the engine works on the server's frames, while a
    /// worker lease carries only ffmpeg arguments, so a worker-delivered candidate would arrive
    /// source-sized and silently pass verification — a replacement without the upscaling that
    /// was asked for. Local-only is the only placement that keeps the promise honest.
    /// </summary>
    internal static WorkPlacement EffectivePlacement(
        WorkPlacement placement, bool superResolutionEnabled, MediaKind kind) =>
        superResolutionEnabled && kind == MediaKind.Video ? WorkPlacement.LocalOnly : placement;

    /// <summary>
    /// The three-stage super-resolution run. Returns the reassembly's <see cref="FfmpegRun"/>,
    /// so every existing failure, verification and replacement behaviour applies unchanged to
    /// the candidate this pipeline produced.
    /// </summary>
    private async Task<FfmpegRun> RunSuperResolutionAsync(
        int jobId,
        JobWork work,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<SettingsStore>();
        var options = await settings.GetSuperResolutionOptionsAsync(cancellationToken);

        RefuseUnsuitableSource(work, options);

        var detection = await new RealCuganDetectionService(options).DetectAsync(cancellationToken);
        if (detection.Readiness != RealCuganReadiness.Ready || detection.BinaryPath is null || detection.ModelsRoot is null)
        {
            throw new InvalidOperationException(
                "Super-resolution is enabled for this library but the engine is not usable: " +
                string.Join(" ", detection.Problems));
        }

        var plan = PlanSuperResolution(work, options, detection.ModelsRoot);
        logger.LogInformation(
            "Job {JobId}: super-resolution pipeline — {Frames} frames, engine {Engine}, preset {Preset}, scale {Scale}x, noise {Noise}, device {Device}",
            jobId, plan.ExpectedFrameCount, detection.BinaryPath, options.Preset, options.Scale,
            options.NoiseLevel, options.Device);

        var framesInDirectory = Path.Combine(plan.WorkDirectory, "frames-in");
        var framesOutDirectory = Path.Combine(plan.WorkDirectory, "frames-out");

        try
        {
            // Stage 1 — extract every source frame as a PNG sequence.
            var extractionArguments = plan.ExtractionArguments;
            await UpdateStageArgumentsAsync(jobId, extractionArguments, cancellationToken);
            var extractionRun = await RunFfmpegAsync(
                jobId,
                Path.Combine(framesInDirectory, RealCuganCommandBuilder.FramePattern),
                extractionArguments,
                work.DurationSeconds,
                plan.ExpectedFrameCount,
                hardwareEncoder: false,
                cancellationToken,
                reportProgress: true,
                progressMap: fraction => fraction * ExtractionProgressShare);
            if (extractionRun.ExitCode != 0)
            {
                return new FfmpegRun(extractionRun.ExitCode, extractionRun.Error,
                    $"frame extraction failed: {extractionRun.Log}");
            }

            var extractedFrames = CountFrames(framesInDirectory);
            if (extractedFrames == 0)
            {
                throw new InvalidOperationException(
                    "Frame extraction produced no frames; the source video stream could not be decoded.");
            }

            // Stage 2 — the engine pass, directory to directory.
            var engineArguments = RealCuganCommandBuilder.BuildEngineArguments(
                framesInDirectory, framesOutDirectory, detection.ModelsRoot, options);
            await UpdateStageArgumentsAsync(jobId, engineArguments, cancellationToken);
            var engineRun = await RunEngineAsync(
                jobId,
                detection.BinaryPath,
                engineArguments,
                framesOutDirectory,
                extractedFrames,
                cancellationToken);
            if (engineRun.ExitCode != 0)
            {
                return engineRun;
            }

            var upscaledFrames = CountFrames(framesOutDirectory);
            if (upscaledFrames != extractedFrames)
            {
                throw new InvalidOperationException(
                    $"The engine produced {upscaledFrames} frames for {extractedFrames} extracted frames; " +
                    "reassembling would drop or duplicate pictures.");
            }

            // Stage 3 — reassemble the upscaled frames with the source's other streams.
            await UpdateStageArgumentsAsync(jobId, plan.ReassemblyArguments, cancellationToken);
            return await RunFfmpegAsync(
                jobId,
                work.Spec.OutputPath,
                plan.ReassemblyArguments,
                work.DurationSeconds,
                plan.ExpectedFrameCount,
                hardwareEncoder: false,
                cancellationToken,
                reportProgress: true,
                progressMap: fraction => ExtractionProgressShare + EngineProgressShare
                    + fraction * ReassemblyProgressShare);
        }
        finally
        {
            // The frame trees are pure scratch — gigabytes per job — and must never outlive the
            // attempt, success or failure. The candidate itself stays for verification.
            DeleteDirectoryQuietly(framesInDirectory);
            DeleteDirectoryQuietly(framesOutDirectory);
            DeleteDirectoryQuietly(plan.WorkDirectory);
        }
    }

    /// <summary>Everything decided before a single process starts: paths, commands, and the
    /// frame count the progress reporting and the engine's output are held to.</summary>
    internal sealed record SuperResolutionPlan(
        string WorkDirectory,
        IReadOnlyList<string> ExtractionArguments,
        IReadOnlyList<string> ReassemblyArguments,
        int ExpectedFrameCount);

    internal static SuperResolutionPlan PlanSuperResolution(
        JobWork work, SuperResolutionOptions options, string modelsRoot)
    {
        var frameRate = work.VideoFrameRate
            ?? throw new InvalidOperationException(
                "Super-resolution needs the source's frame rate, which the probe does not report. Re-probe the file and retry.");
        var frameCount = work.FrameCount
            ?? throw new InvalidOperationException(
                "Super-resolution needs the source's frame count, which the probe does not report. Re-probe the file and retry.");

        var workDirectory = Path.Combine(
            Path.GetDirectoryName(work.Spec.OutputPath)!, "realcugan");

        return new SuperResolutionPlan(
            workDirectory,
            RealCuganCommandBuilder.BuildFrameExtraction(
                work.Spec.InputPath, Path.Combine(workDirectory, "frames-in"), frameRate, work.CpuThreadLimit),
            RealCuganCommandBuilder.BuildReassembly(
                work.Spec, work.VideoEncoder!, Path.Combine(workDirectory, "frames-out"), frameRate,
                work.CpuThreadLimit, OptimisedMarkerValue),
            frameCount);
    }

    /// <summary>A source the pipeline cannot faithfully upscale is refused with a clear reason
    /// before anything runs — never silently processed into a wrong result.</summary>
    internal static void RefuseUnsuitableSource(JobWork work, SuperResolutionOptions options)
    {
        var problems = options.Validate();
        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                "The super-resolution configuration is not valid: " + problems[0].Message);
        }
        // The candidate's expected size is the promise verification holds the output to. A
        // source whose dimensions were never probed gives no honest expectation, and guessing
        // one would turn the dimension gate into either a rubber stamp or a false accusation.
        if (work.SourcePicture is not { } sourcePicture
            || sourcePicture.Width <= 0 || sourcePicture.Height <= 0)
        {
            throw new InvalidOperationException(
                "Super-resolution needs the source's picture dimensions, which the probe does not report. " +
                "Re-probe the file and retry.");
        }
        if (work.Original.IsHdr)
        {
            throw new InvalidOperationException(
                "Super-resolution does not support HDR sources: the frame extraction would fold the " +
                "HDR picture into SDR. Leave the file to the normal pipeline.");
        }
        if (work.Spec.SourceIsVariableFrameRate)
        {
            throw new InvalidOperationException(
                "Super-resolution does not support variable-frame-rate sources: reassembling the " +
                "upscaled frames at a constant rate would mistime them.");
        }
        if (work.Spec.CropTo is { } crop)
        {
            throw new InvalidOperationException(
                $"This library's rules crop the picture to {crop.Width}x{crop.Height}, which a " +
                "super-resolution job does not apply. Remove the crop for this library or leave the file to the normal pipeline.");
        }
        if (work.Spec.DownscaleTo is { } downscale)
        {
            throw new InvalidOperationException(
                $"This library's rules downscale to {downscale.Width}x{downscale.Height}, which a " +
                "super-resolution job contradicts: the engine upscales, it does not shrink. Remove the downscale or leave the file to the normal pipeline.");
        }
        if (work.Spec.FrameRate is { } decimation)
        {
            throw new InvalidOperationException(
                "This library's rules cap the frame rate, which a super-resolution job does not " +
                "apply: the engine runs on every source frame. Remove the cap or leave the file to the normal pipeline.");
        }
        if (IsHardwareEncoder(work.VideoEncoder))
        {
            throw new InvalidOperationException(
                $"Super-resolution reassembles with the library's encoder ({work.VideoEncoder}), which is a " +
                "hardware encoder needing its own upload filters on extracted frames. Switch the queue's " +
                "encoder mode to software, or leave this file to the normal pipeline.");
        }
    }

    /// <summary>
    /// Runs the engine over the extracted frames with a dedicated process: argument array, both
    /// output streams captured, cancellation honoured, progress from the frames appearing in the
    /// output directory, and a stall limit that turns a hung GPU pass into a failed attempt.
    /// The queue's shares, timings and process tracking bind the generic run below to a job.
    /// </summary>
    private Task<FfmpegRun> RunEngineAsync(
        int jobId,
        string binaryPath,
        IReadOnlyList<string> arguments,
        string framesOutDirectory,
        int expectedFrames,
        CancellationToken cancellationToken) =>
        RunEngineProcessAsync(
            binaryPath,
            arguments,
            framesOutDirectory,
            expectedFrames,
            EngineProgressPollInterval,
            EngineStallLimit,
            trackStarted: processId =>
            {
                // Make the engine visible to the metrics broadcaster and to a manual pause, the
                // same way an ffmpeg run does; the caller owns the registration's lifetime only
                // for as long as this process runs.
                var registration = encodes.Track(processId, hardwareEncoder: false);
                pauseManager.OnEncodeStarted(processId);
                return registration;
            },
            reportStage: fraction =>
                UpdateProgressAsync(jobId, ExtractionProgressShare + fraction * EngineProgressShare),
            cancellationToken: cancellationToken);

    /// <summary>
    /// One engine pass as a bare child process. Static, with the timings and the job-bound hooks
    /// injected, so the failure modes a real engine can produce are testable against a real
    /// process without a queue behind them: a non-zero exit is reported with its code and its
    /// captured output, a pass that stops producing frames is killed at the stall limit, and a
    /// cancelled token kills the whole process tree.
    /// </summary>
    internal static async Task<FfmpegRun> RunEngineProcessAsync(
        string binaryPath,
        IReadOnlyList<string> arguments,
        string framesOutDirectory,
        int expectedFrames,
        TimeSpan pollInterval,
        TimeSpan stallLimit,
        Func<int, IDisposable?>? trackStarted,
        Func<double, Task>? reportStage,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(framesOutDirectory);
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(binaryPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        var output = new System.Text.StringBuilder();
        var error = new System.Text.StringBuilder();
        process.OutputDataReceived += (_, received) => { var line = received.Data; if (line is not null) lock (output) output.AppendLine(line); };
        process.ErrorDataReceived += (_, received) => { var line = received.Data; if (line is not null) lock (error) error.AppendLine(line); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var registration = trackStarted?.Invoke(process.Id);

        // A cancelled job kills the engine's whole process tree; the frame trees go with the
        // finally block in the caller.
        await using var cancellation = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        });

        var lastAdvance = DateTimeOffset.UtcNow;
        var lastCount = 0;
        while (!process.HasExited)
        {
            await Task.Delay(pollInterval, cancellationToken);
            var done = CountFrames(framesOutDirectory);
            if (done > lastCount)
            {
                lastCount = done;
                lastAdvance = DateTimeOffset.UtcNow;
            }
            else if (DateTimeOffset.UtcNow - lastAdvance > stallLimit)
            {
                process.Kill(entireProcessTree: true);
                return new FfmpegRun(-1,
                    $"The super-resolution engine made no progress for {stallLimit.TotalMinutes:N0} minutes " +
                    $"and was stopped at frame {lastCount} of {expectedFrames}.",
                    error.ToString());
            }

            // Extraction owns the first share of the bar; the engine owns the share that follows.
            if (reportStage is not null)
            {
                var stageFraction = expectedFrames > 0 ? Math.Min(1.0, (double)done / expectedFrames) : 0.0;
                await reportStage(stageFraction);
            }
        }

        await process.WaitForExitAsync(CancellationToken.None);
        var log = output.ToString() + error.ToString();
        if (process.ExitCode != 0)
        {
            return new FfmpegRun(process.ExitCode,
                $"The super-resolution engine exited with code {process.ExitCode}.", log);
        }

        return new FfmpegRun(process.ExitCode, null, log);
    }

    private static int CountFrames(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*.png"))
        {
            if (Path.GetFileName(file).StartsWith('f'))
            {
                count++;
            }
        }
        return count;
    }

    private static void DeleteDirectoryQuietly(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Scratch that will not go away is reclaimed by the startup recovery sweep; a
            // leftover frame tree must never fail a job that otherwise succeeded.
        }
    }

    private Task UpdateStageArgumentsAsync(int jobId, IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
        WithJobAsync(jobId, job =>
        {
            job.FfmpegArguments = string.Join(' ', arguments);
            job.UpdatedAt = DateTimeOffset.UtcNow;
        }, cancellationToken);
}
