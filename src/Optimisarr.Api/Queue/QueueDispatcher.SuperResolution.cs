using System.Diagnostics;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Tools;
using Optimisarr.Core.Queue;
using Optimisarr.Core.SuperResolution;
using Optimisarr.Core.Rules;

namespace Optimisarr.Api.Queue;

/// <summary>
/// The super-resolution pipeline: a video job's frames are extracted as a PNG
/// sequence, upscaled by the engine directory-to-directory, and reassembled with the source's
/// audio, subtitles and attachments into the candidate the normal verification and replacement
/// flow then judges. It reuses <see cref="QueueDispatcher"/>'s process handling, progress
/// reporting, work-directory reservation and cleanup — a super-resolution job is the same job,
/// with a three-stage encode.
/// </summary>
public sealed partial class QueueDispatcher
{
    /// <summary>Nothing — no picture decoded, upscaled or encoded — for this long means the
    /// attempt is never going to finish. A paused queue does not count.</summary>
    internal static readonly TimeSpan SuperResolutionStallLimit = TimeSpan.FromMinutes(10);

    /// <summary>The engine options and quality check frozen before this attempt is planned.</summary>
    internal sealed record SuperResolutionChoice(SuperResolutionOptions Options, SuperResolutionQualityCheck QualityCheck);

    /// <summary>The pipeline copies audio; container selection must use that contract too.</summary>
    internal static RuleSettings SuperResolutionRules(RuleSettings saved, bool enabledForAttempt) =>
        enabledForAttempt ? saved with
        {
            VideoAudioCodec = null,
            DownmixToStereo = false,
            NormalizeOpusChannelLayout = false,
        } : saved;

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
    /// The routing decision as pure facts: the job's shape and <see cref="SuperResolutionRouting"/>,
    /// the one rule every placement and claim decision shares. With the feature off, an opted-in
    /// library's jobs take exactly the path they always took.
    /// </summary>
    internal static bool RoutesThroughSuperResolution(
        JobWork work, SuperResolutionOptions options, bool libraryOptedIn) =>
        IsSuperResolutionCandidate(work)
        && SuperResolutionRouting.Applies(options.Enabled, libraryOptedIn, work.Spec.Kind, replaceBound: true);

    /// <summary>
    /// The placement a job effectively runs under. Super-resolution work runs on this server only:
    /// the engine works on the server's frames, while a worker lease carries only ffmpeg arguments,
    /// so a worker-delivered candidate would arrive source-sized and silently pass verification —
    /// a replacement without the upscaling that was asked for. Everything else keeps the
    /// operator's placement.
    /// </summary>
    internal static WorkPlacement EffectivePlacement(WorkPlacement placement, bool superResolution) =>
        superResolution ? WorkPlacement.LocalOnly : placement;

    /// <summary>
    /// The work an upscaled attempt is judged as: the candidate is expected at the upscaled size,
    /// verification follows the library's quality check with the size-saving gates off, the size
    /// forecast is skipped, and the result always waits for a person to approve its replacement.
    /// </summary>
    internal static JobWork PrepareSuperResolutionWork(
        JobWork work, SuperResolutionOptions options, SuperResolutionQualityCheck qualityCheck)
    {
        var upscaled = options.UpscaledSize(work.SourcePicture)
            ?? throw new InvalidOperationException(
                "Super-resolution needs the source's picture dimensions, which the probe does not report. Re-probe the file and retry.");
        return work with
        {
            VerificationPolicy = SuperResolutionPolicy.ForUpscaledCandidate(work.VerificationPolicy, qualityCheck),
            // The encoder copies audio untouched, so nothing is re-encoded, downmixed or filtered,
            // and no soundtrack bitrate search runs.
            Original = work.Original with
            {
                ExpectedWidth = upscaled.Width,
                ExpectedHeight = upscaled.Height,
                AudioReencoded = false,
                AudioDownmixed = false,
                AudioFiltered = false,
                ExpectedAudioCodec = null,
            },
            AdaptiveSoundtrackQuality = false,
            BypassSizePreflight = true,
            AutoReplace = false,
            MoveOnComplete = false,
            TargetFolder = null,
            MoveOverwrite = false,
        };
    }

    /// <summary>
    /// One super-resolution attempt as a bounded stream (see <see cref="SuperResolutionPipeline"/>).
    /// Returns the encoder's <see cref="FfmpegRun"/>, so every existing failure, verification and
    /// replacement behaviour applies unchanged to the candidate it produced.
    /// </summary>
    private async Task<FfmpegRun> RunSuperResolutionAsync(
        int jobId,
        JobWork work,
        SuperResolutionOptions options,
        CancellationToken cancellationToken)
    {
        // The options were frozen when this attempt was planned: the expected dimensions
        // verification holds the candidate to came from them, so a settings edit mid-attempt
        // must not change the engine run.
        RefuseUnsuitableSource(work, options);

        var detection = await new SuperResolutionDetectionService(options).DetectAsync(cancellationToken);
        if (detection.Readiness != SuperResolutionReadiness.Ready || detection.BinaryPath is null || detection.ModelsRoot is null)
        {
            throw new InvalidOperationException(
                "Super-resolution is enabled for this library but the engine is not usable: " +
                string.Join(" ", detection.Problems));
        }

        var source = await ProbeSuperResolutionSourceAsync(work.Spec.InputPath, cancellationToken, work.Spec.Deinterlace);
        if (source.Refusal is { } refusal)
        {
            throw new InvalidOperationException(refusal + " Leave the file to the normal pipeline.");
        }
        var frameRate = source.FrameRate ?? work.VideoFrameRate ?? throw new InvalidOperationException(
            "Super-resolution needs the source's frame rate, which the probe does not report. Re-probe the file and retry.");
        var sourcePicture = work.SourcePicture!;
        var upscaled = options.UpscaledSize(sourcePicture)!;

        var queue = await GetQueueSettingsAsync(cancellationToken);
        var free = WorkPaths.TryGetAvailableFreeSpace(_workRoot);
        var framesPerBatch = SuperResolutionBatchPlan.FramesPerBatch(
            sourcePicture, options.Scale, free ?? long.MaxValue, queue.MinFreeDiskBytes);
        var batchBytes = SuperResolutionBatchPlan.MinimumFrames
            * SuperResolutionBatchPlan.WorstCaseBytesPerFrame(sourcePicture, options.Scale);
        if (framesPerBatch is not { } frames)
        {
            throw new InvalidOperationException(
                $"Super-resolution needs about {batchBytes / (1024.0 * 1024 * 1024):0.#} GiB of working space above the " +
                "free-space reserve for even a small batch of pictures. Free some space on the work volume and retry.");
        }

        var encoderArguments = SuperResolutionCommandBuilder.BuildEncoder(
            work.Spec, work.VideoEncoder!, frameRate, work.CpuThreadLimit, OptimisedMarkerValue, source.VideoStartSeconds,
            source.BitDepth, source.Colour!);
        await UpdateStageArgumentsAsync(jobId, encoderArguments, cancellationToken);
        logger.LogInformation(
            "Job {JobId}: super-resolution — {Frames} pictures per batch, {Run}, noise {Noise}, device {Device}",
            jobId, frames, options.RunDescription, options.NoiseLevel, options.Device);

        using var outputDirectory = WorkPaths.PrepareOutputDirectory(work.Spec.OutputPath, ReserveWorkDirectory);
        var request = new SuperResolutionRunRequest(
            transcodeOptions.Ffmpeg,
            detection.BinaryPath,
            SuperResolutionCommandBuilder.BuildDecoder(work.Spec.InputPath, work.CpuThreadLimit, source.BitDepth, source.Colour!, work.Spec.Deinterlace),
            encoderArguments,
            (input, output) => options.Engine.BuildArguments(input, output, detection.ModelsRoot, options),
            WorkOutputRoot.ForSuperResolution(_workRoot, jobId, Guid.NewGuid()),
            upscaled,
            work.FrameCount,
            frames,
            frames * SuperResolutionBatchPlan.WorstCaseBytesPerFrame(sourcePicture, options.Scale),
            queue.MinFreeDiskBytes,
            SuperResolutionStallLimit);
        var hooks = new SuperResolutionRunHooks(
            () => WorkPaths.TryGetAvailableFreeSpace(_workRoot),
            () => pauseManager.IsPaused,
            processId =>
            {
                // Visible to the metrics broadcaster, and suspended by a manual pause like any encode.
                var registration = encodes.Track(processId, hardwareEncoder: false);
                pauseManager.OnEncodeStarted(processId);
                return registration;
            },
            fraction => UpdateProgressAsync(jobId, fraction));
        var run = await SuperResolutionPipeline.RunAsync(request, hooks, cancellationToken);
        if (run.ExitCode != 0 || source.AttachmentCount == 0 || IsMp4Family(work.Spec.OutputPath))
        {
            return run;
        }

        // Verification does not compare attachments, so the subtitle fonts and other attachments
        // this pipeline promises to keep are checked here, before the candidate is judged.
        var candidate = await ProbeSuperResolutionSourceAsync(work.Spec.OutputPath, cancellationToken);
        return candidate.AttachmentCount >= source.AttachmentCount
            ? run
            : run with
            {
                ExitCode = -1,
                Error = $"The candidate kept {candidate.AttachmentCount} of the source's {source.AttachmentCount} attachments, " +
                    "such as subtitle fonts; it was not accepted.",
            };
    }

    private static bool IsMp4Family(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".mp4" or ".m4v" or ".mov";

    /// <summary>One ffprobe of a file's streams, read by <see cref="SuperResolutionSource"/>.</summary>
    private async Task<SuperResolutionSource> ProbeSuperResolutionSourceAsync(string path, CancellationToken cancellationToken, bool deinterlaced = false)
    {
        var ffprobe = MediaToolCommands.ResolveFfprobe(Environment.GetEnvironmentVariable("OPTIMISARR_FFPROBE"), transcodeOptions.Ffmpeg);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(ffprobe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        foreach (var argument in new[] { "-v", "error", "-show_streams", "-show_format", "-of", "json", path })
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        process.Start();
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"Probing {Path.GetFileName(path)} for super-resolution failed: {(await error).Trim()}");
            }
            return SuperResolutionSource.Parse(await output, deinterlaced);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
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

    private Task UpdateStageArgumentsAsync(int jobId, IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
        WithJobAsync(jobId, job =>
        {
            job.FfmpegArguments = string.Join(' ', arguments);
            job.UpdatedAt = DateTimeOffset.UtcNow;
        }, cancellationToken);
}
