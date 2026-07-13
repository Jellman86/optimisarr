using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Library;
using Optimisarr.Api.Realtime;
using Optimisarr.Api.Replacement;
using Optimisarr.Core.Activity;
using Optimisarr.Core.Library;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Scheduling;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Tools;
using Optimisarr.Core.Verification;
using Optimisarr.Data;

namespace Optimisarr.Api.Queue;

/// <summary>
/// The transcode worker. A single background loop owns all job-state transitions
/// (SQLite has one writer), selecting work via the pure <see cref="JobScheduler"/>
/// and running ffmpeg out-of-process. A job only ever writes to the work directory;
/// it never deletes or overwrites the original — safe replacement is a later phase.
/// </summary>
public sealed class QueueDispatcher(
    IServiceScopeFactory scopeFactory,
    IHubContext<JobsHub> hub,
    IHostEnvironment environment,
    VerificationService verification,
    HardwareCapabilityService hardware,
    ActivityMonitor activityMonitor,
    ImageMarkerService imageMarker,
    TranscodeOptions transcodeOptions,
    ActiveEncodeRegistry encodes,
    ILogger<QueueDispatcher> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
    private const int MaxAttempts = 3;

    // A video preview encodes only this many seconds — enough to judge quality/size without
    // paying for a full transcode of a long file.
    private const int PreviewClipSeconds = 60;

    // Stamped into every output's container metadata so the file proves it was optimised
    // independently of the database; see OptimisationMarker.
    private static readonly string OptimisedMarkerValue =
        typeof(QueueDispatcher).Assembly.GetName().Version?.ToString() ?? "unknown";

    private static readonly JsonSerializerOptions ReportJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _workRoot = WorkPaths.Resolve(environment);
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _running = new();
    private readonly SemaphoreSlim _dbLock = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);

    /// <summary>Nudges the loop to dispatch immediately (e.g. right after enqueue).</summary>
    public void Wake()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { /* already signalled */ }
    }

    /// <summary>Stops the ffmpeg process backing a running job, if any.</summary>
    public void RequestCancel(int jobId)
    {
        if (_running.TryGetValue(jobId, out var cts))
        {
            cts.Cancel();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await PurgePreviewsAsync(stoppingToken);
        await RecoverInterruptedJobsAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchAsync(stoppingToken);
                // Apply "Replace automatically" retrospectively: jobs already in ReadyToReplace when
                // the toggle was turned on (or left there by a transient replace failure) are picked
                // up here, not just jobs that verify after the toggle.
                await ReconcileAutoReplaceAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Queue dispatch loop error");
            }

            try { await _wake.WaitAsync(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    // Previews are throwaway and must not survive a restart: drop every preview job row and wipe
    // the whole preview scratch subtree so disk never accumulates abandoned comparison outputs.
    private async Task PurgePreviewsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();

        var previews = await db.Jobs.Where(job => job.Type == JobType.Preview).ToListAsync(cancellationToken);
        if (previews.Count > 0)
        {
            db.Jobs.RemoveRange(previews);
            await db.SaveChangesAsync(cancellationToken);
        }

        var previewRoot = $"{_workRoot.TrimEnd('/', '\\')}/preview";
        try
        {
            if (Directory.Exists(previewRoot))
            {
                Directory.Delete(previewRoot, recursive: true);
            }
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Could not purge preview work directory {Path}", previewRoot);
        }
    }

    // After a restart no worker is alive, so any job left mid-flight is reset to
    // Queued (or failed after too many attempts). Stale outputs are cleaned up.
    private async Task RecoverInterruptedJobsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();

        var interrupted = await db.Jobs
            .Where(job => job.Status == JobStatus.Probing
                || job.Status == JobStatus.Transcoding
                || job.Status == JobStatus.Verifying)
            .ToListAsync(cancellationToken);

        if (interrupted.Count == 0)
        {
            return;
        }

        foreach (var job in interrupted)
        {
            DeleteWorkOutput(job.WorkOutputPath);
            job.Progress = 0;
            job.WorkOutputPath = null;
            job.UpdatedAt = DateTimeOffset.UtcNow;

            if (job.Attempt >= MaxAttempts)
            {
                job.Status = JobStatus.Failed;
                job.ErrorMessage = "Interrupted too many times (worker restarted while running).";
                job.FinishedAt = DateTimeOffset.UtcNow;
            }
            else
            {
                job.Status = JobStatus.Queued;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Recovered {Count} interrupted job(s) after restart", interrupted.Count);
        await NotifyAsync();
    }

    // Cap per cycle so a large backlog of ready jobs is replaced gradually rather than in one burst.
    private const int AutoReplaceReconcileBatch = 20;

    // Replaces jobs already in ReadyToReplace whose library auto-replaces. This makes "Replace
    // automatically" apply retrospectively (a job that verified before the toggle was on, or was
    // left ready by a transient replace failure). ReplaceAsync still quarantines the original and
    // records a rollback first, so the safety model is unchanged; a failure leaves the job ready
    // for the next cycle to retry.
    private async Task ReconcileAutoReplaceAsync(CancellationToken cancellationToken)
    {
        List<int> jobIds;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            var settings = scope.ServiceProvider.GetRequiredService<SettingsStore>();
            var queueSettings = await settings.GetQueueSettingsAsync(cancellationToken);
            var ready = await db.Jobs
                .AsNoTracking()
                .Where(job => job.Status == JobStatus.ReadyToReplace && job.LibraryId != null)
                .Join(
                    db.Libraries,
                    job => job.LibraryId,
                    library => library.Id,
                    (job, library) => new { job.Id, job.Status, job.VerificationPassed, library.AutoReplace })
                .ToListAsync(cancellationToken);

            jobIds = ready
                .Where(candidate => AutoReplacePolicy.ShouldReconcile(
                    candidate.Status,
                    candidate.VerificationPassed,
                    candidate.AutoReplace,
                    queueSettings.DryRunMode))
                .OrderBy(candidate => candidate.Id)
                .Take(AutoReplaceReconcileBatch)
                .Select(candidate => candidate.Id)
                .ToList();
        }

        if (jobIds.Count == 0)
        {
            return;
        }

        var replaced = 0;
        foreach (var jobId in jobIds)
        {
            await using var replaceScope = scopeFactory.CreateAsyncScope();
            var replacement = replaceScope.ServiceProvider.GetRequiredService<ReplacementService>();
            var result = await replacement.ReplaceAsync(jobId, cancellationToken);
            if (result.Kind == ReplacementResultKind.Success)
            {
                replaced++;
                logger.LogInformation("Job {JobId}: auto-replaced the original (library auto-replace, reconciled).", jobId);
            }
            else if (result.Permanent)
            {
                // A permanently blocked replacement (the verified output vanished, the original is
                // gone, or a different optimised file occupies the destination) can never succeed on
                // retry. Reconciling it every cycle would loop forever and bury real warnings, so fail
                // it once. Replacement leaves the original untouched in each of these cases.
                await CompleteAsync(jobId, JobStatus.Failed, error: result.Message);
                logger.LogWarning(
                    "Job {JobId}: auto-replace cannot complete ({Kind}): {Message}. Marked Failed (will not retry).",
                    jobId, result.Kind, result.Message);
            }
            else
            {
                logger.LogWarning(
                    "Job {JobId}: auto-replace reconcile did not complete ({Kind}): {Message}. Left ReadyToReplace.",
                    jobId, result.Kind, result.Message);
            }
        }

        if (replaced > 0)
        {
            await NotifyAsync();
        }
    }

    private async Task DispatchAsync(CancellationToken stoppingToken)
    {
        var settings = await GetQueueSettingsAsync(stoppingToken);
        var activity = await activityMonitor.GetActivityAsync(stoppingToken);
        var policy = EvaluateDispatchPolicy(settings, activity);
        if (!policy.CanStart)
        {
            logger.LogDebug("Queue dispatch paused: {Reason}", policy.BlockedReason);
            return;
        }

        var maxConcurrent = settings.MaxConcurrentJobs;
        if (maxConcurrent - _running.Count <= 0)
        {
            return;
        }

        List<QueuedJob> queued;
        Dictionary<int, (TimeOnly Start, TimeOnly End)> autoWindows;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            queued = await db.Jobs
                .AsNoTracking()
                .Where(job => job.Status == JobStatus.Queued)
                .Select(job => new QueuedJob(job.Id, job.LibraryId, job.Priority, job.EnqueuedAt))
                .ToListAsync(stoppingToken);

            // A library that auto-optimises only runs its jobs inside its window; a library with
            // auto-optimise off has no window, so its (manually enqueued) jobs may run anytime.
            autoWindows = await db.Libraries
                .AsNoTracking()
                .Where(library => library.AutoEnqueueEnabled)
                .Select(library => new { library.Id, library.AutoEnqueueWindowStart, library.AutoEnqueueWindowEnd })
                .ToDictionaryAsync(
                    library => library.Id,
                    library => (library.AutoEnqueueWindowStart, library.AutoEnqueueWindowEnd),
                    stoppingToken);
        }

        var nowLocal = TimeOnly.FromDateTime(DateTime.Now);
        var runnable = queued
            .Where(job => job.LibraryId is not { } libraryId
                || !autoWindows.TryGetValue(libraryId, out var window)
                || DispatchPolicyEvaluator.WithinWindow(window.Start, window.End, nowLocal))
            .ToList();

        var toStart = JobScheduler.SelectJobsToStart(runnable, _running.Count, maxConcurrent);
        foreach (var jobId in toStart)
        {
            if (_running.ContainsKey(jobId) || !await TryClaimAsync(jobId, stoppingToken))
            {
                continue;
            }

            var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            _running[jobId] = cts;
            _ = Task.Run(() => RunJobAsync(jobId, cts.Token), CancellationToken.None);
        }
    }

    // Single-writer claim: only transition if still Queued, so a job can never start twice.
    private async Task<bool> TryClaimAsync(int jobId, CancellationToken cancellationToken)
    {
        await _dbLock.WaitAsync(cancellationToken);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);
            if (job is null || job.Status != JobStatus.Queued)
            {
                return false;
            }

            job.Status = JobStatus.Transcoding;
            job.Attempt += 1;
            job.StartedAt = DateTimeOffset.UtcNow;
            job.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        finally
        {
            _dbLock.Release();
        }
    }

    private async Task RunJobAsync(int jobId, CancellationToken cancellationToken)
    {
        try
        {
            var work = await LoadWorkAsync(jobId, cancellationToken);
            if (work is null)
            {
                await CompleteAsync(jobId, JobStatus.Failed, error: "Job or media file no longer exists.");
                return;
            }

            // The source can vanish between scan/enqueue and now — e.g. Radarr/Sonarr upgraded and
            // renamed the file. Fail fast with a clear, actionable message instead of a raw ffmpeg
            // "No such file or directory" (and skip a pointless hardware-encoder init). The next
            // library scan prunes the stale inventory row.
            if (!File.Exists(work.Value.Original.Path))
            {
                await CompleteAsync(jobId, JobStatus.Failed, error:
                    $"Source file no longer exists: {work.Value.Original.Path}. It was most likely moved or "
                    + "upgraded by your media manager (Radarr/Sonarr). Re-scan the library and the stale entry "
                    + "will be removed.");
                return;
            }

            // Pre-flight eligibility re-check: a job can sit in a long backlog while the library's
            // rules tighten (e.g. the already-efficient-source floor) or the file gains an optimised
            // sibling. Re-evaluate against the current rules and skip rather than burn an encode the
            // size-saving gate would only reject. Previews always run — they exist to show settings.
            if (!work.Value.IsPreview)
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var candidates = scope.ServiceProvider.GetRequiredService<CandidateService>();
                var decision = await candidates.EvaluateFileAsync(work.Value.MediaFileId, cancellationToken);
                if (decision is { IsEligible: false })
                {
                    await CompleteAsync(jobId, JobStatus.Cancelled,
                        error: $"Skipped before encoding: {decision.Reason}");
                    logger.LogInformation(
                        "Job {JobId}: skipped before encoding — {Reason}", jobId, decision.Reason);
                    return;
                }
            }

            var (spec, arguments) = work.Value;
            var hardwareEncoder = IsHardwareEncoder(work.Value.VideoEncoder);
            Directory.CreateDirectory(Path.GetDirectoryName(spec.OutputPath)!);
            await BeginTranscodeAsync(jobId, spec.OutputPath, arguments, work.Value.VideoEncoder, cancellationToken);
            await NotifyAsync();

            // A clipped preview only encodes the clip window, so progress is measured against that,
            // not the full runtime (otherwise the bar would barely move then jump to done).
            var progressDuration = spec.ClipSeconds is { } clip && (work.Value.DurationSeconds is not { } d || clip < d)
                ? clip
                : work.Value.DurationSeconds;
            var run = await RunFfmpegAsync(jobId, arguments, progressDuration, hardwareEncoder, cancellationToken);

            // A hardware-decode attempt can fail on a source the GPU cannot decode (an exotic
            // codec or profile). Retry once with the software-decode command before giving up,
            // so the job degrades to CPU decode instead of failing outright.
            if (run.ExitCode != 0
                && work.Value.SoftwareDecodeArguments is { } softwareArguments
                && HardwareDecodeFallback.ShouldRetryInSoftware(run.Error))
            {
                logger.LogWarning(
                    "Job {JobId}: hardware decode failed; retrying with software decode. ffmpeg: {Error}",
                    jobId, run.Error);
                DeleteWorkOutput(spec.OutputPath);
                arguments = softwareArguments;
                await BeginTranscodeAsync(jobId, spec.OutputPath, arguments, work.Value.VideoEncoder, cancellationToken);
                run = await RunFfmpegAsync(jobId, arguments, progressDuration, hardwareEncoder, cancellationToken);
            }

            if (run.ExitCode == 0)
            {
                // Stamp the portable marker on an image *before* verification, so the file that is
                // verified and replaced is the final, marked file. ffmpeg drops -metadata for
                // stills, so this is done out-of-band with exiftool; a failure only loses marker
                // portability (the DB history still prevents re-optimisation), so it never blocks.
                if (spec.Kind == MediaKind.Image)
                {
                    if (!await imageMarker.CopyMetadataAsync(
                            work.Value.Original.Path, spec.OutputPath, cancellationToken))
                    {
                        logger.LogWarning(
                            "Job {JobId}: could not copy source EXIF/ICC metadata; the default metadata " +
                            "verification gate will reject the output if the source carried either.", jobId);
                    }

                    if (!await imageMarker.WriteAsync(spec.OutputPath, OptimisedMarkerValue, cancellationToken))
                    {
                        logger.LogWarning(
                            "Job {JobId}: could not write the portable image marker (exiftool missing or failed); " +
                            "re-optimisation is still prevented by the database.", jobId);
                    }
                }

                await VerifyAndFinishAsync(jobId, spec.OutputPath, work.Value, cancellationToken);
            }
            else
            {
                DeleteWorkOutput(spec.OutputPath);
                // Translate known ffmpeg failures into a clear, actionable reason; fall back to the
                // raw stderr tail for anything unrecognised.
                var error = FfmpegErrorInterpreter.Explain(run.Error)
                    ?? run.Error
                    ?? $"ffmpeg exited with code {run.ExitCode}";
                await CompleteAsync(jobId, JobStatus.Failed, error: error, processLog: run.Log);
                await NotifyJobFailedAsync(jobId, error);
            }
        }
        catch (OperationCanceledException)
        {
            await HandleCancelledAsync(jobId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Job {JobId} failed", jobId);
            await CompleteAsync(jobId, JobStatus.Failed, error: ex.Message);
            await NotifyJobFailedAsync(jobId, ex.Message);
        }
        finally
        {
            if (_running.TryRemove(jobId, out var cts))
            {
                cts.Dispose();
            }
            await NotifyAsync();
            Wake(); // a slot just freed up
        }
    }

    private readonly record struct JobWork(
        TranscodeSpec Spec,
        IReadOnlyList<string> Arguments,
        string? VideoEncoder,
        bool IsPreview,
        double? DurationSeconds,
        bool MoveOnComplete,
        string? TargetFolder,
        bool MoveOverwrite,
        int MediaFileId,
        OriginalSnapshot Original,
        double? MinVmafHarmonicMean,
        double? MinVmafMin,
        bool AutoReplace,
        // When the primary command hardware-decodes the source, this holds the equivalent
        // software-decode command so the dispatcher can transparently retry a source the GPU
        // cannot decode. Null when hardware decode was not used.
        IReadOnlyList<string>? SoftwareDecodeArguments = null)
    {
        public void Deconstruct(out TranscodeSpec spec, out IReadOnlyList<string> arguments)
        {
            spec = Spec;
            arguments = Arguments;
        }
    }

    private async Task<JobWork?> LoadWorkAsync(int jobId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();

        var job = await db.Jobs
            .AsNoTracking()
            .Include(j => j.MediaFile)!
            .ThenInclude(file => file!.Library)
            .FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);

        if (job?.MediaFile is null)
        {
            return null;
        }

        var media = job.MediaFile;
        var library = media.Library;
        var rules = LibraryRuleResolution.Resolve(library);
        var settings = scope.ServiceProvider.GetRequiredService<SettingsStore>();
        var queueSettings = await settings.GetQueueSettingsAsync(cancellationToken);

        var isPreview = job.Type == JobType.Preview;

        // MP4 can't store image-based subtitles (Blu-ray PGS / DVD VobSub). When a video job
        // targets MP4 and the file has subtitle tracks, probe the source: if any are bitmap, the
        // resolver falls back to MKV so they're preserved instead of failing the encode. Gated so
        // the extra ffprobe only runs when it could matter.
        var sourceHasImageSubtitles = false;
        if (media.MediaKind is not (MediaKind.Audio or MediaKind.Image)
            && (media.SubtitleTrackCount ?? 0) > 0
            && TranscodeSpecResolver.IsMp4Container(rules.TargetContainer))
        {
            var probe = scope.ServiceProvider.GetRequiredService<MediaProbeService>();
            var probeResult = await probe.ProbeAsync(media.Path, cancellationToken);
            sourceHasImageSubtitles = probeResult.HasImageSubtitles;
        }

        // MP4/MOV has no tag for some Blu-ray audio (TrueHD, LPCM); copying one into an MP4 target
        // aborts the encode. The inventory already recorded the source's audio codecs, so this needs
        // no extra probe — the resolver falls back to MKV when such audio would be copied.
        var sourceHasMp4IncompatibleAudio =
            media.MediaKind is not (MediaKind.Audio or MediaKind.Image)
            && TranscodeSpecResolver.IsMp4Container(rules.TargetContainer)
            && AudioContainerCompatibility.ContainsMp4Incompatible(media.AudioCodecs);

        // Each file's output lives under a per-media-file work root so two sources that share a
        // stem but differ by extension can never resolve to the same work path and clobber each
        // other's verified output before it is moved or replaced. A preview writes under its own
        // throwaway tree keyed by job id, kept apart from replace-bound output.
        var spec = TranscodeSpecResolver.Resolve(
            rules,
            media.Path,
            media.RelativePath,
            isPreview
                ? WorkOutputRoot.ForPreview(_workRoot, job.Id)
                : WorkOutputRoot.ForMediaFile(_workRoot, media.Id),
            media.IsHdr,
            library?.QualityCrf ?? rules.DefaultCrf,
            library?.EncoderPreset,
            media.MediaKind,
            sourceHasImageSubtitles,
            sourceHasMp4IncompatibleAudio,
            media.VideoCodec,
            media.MaxAudioChannels,
            media.IsVariableFrameRate == true);

        // A video preview only needs a short sample: encoding the whole file would be as slow as a
        // real transcode. Take it from the middle, where the content is representative rather than an
        // intro/black frames. Audio/image previews are already fast, so they run in full.
        if (isPreview && spec.Kind == MediaKind.Video && spec.VideoCodec is not null)
        {
            var start = media.DurationSeconds is { } duration && duration > PreviewClipSeconds
                ? (int)(duration / 2 - PreviewClipSeconds / 2.0)
                : 0;
            spec = spec with
            {
                ClipSeconds = PreviewClipSeconds,
                ClipStartSeconds = start > 0 ? start : null
            };
        }

        var original = new OriginalSnapshot(
            media.Path,
            media.SizeBytes,
            media.DurationSeconds,
            media.AudioTrackCount ?? 0,
            media.SubtitleTrackCount ?? 0,
            media.IsHdr,
            rules.Hdr == HdrHandling.TonemapToSdr,
            media.MediaKind,
            // A video job whose audio was re-encoded (not copied) may legitimately normalise
            // the sample rate, so the audio-fidelity gate must treat it like an audio job.
            AudioReencoded: media.MediaKind != MediaKind.Audio && spec.AudioEncoder is not null,
            // An operator-requested stereo downmix is an intentional channel reduction.
            AudioDownmixed: spec.DownmixToStereo,
            // A requested image downscale is an intentional dimension reduction, not corruption.
            ImageDownscaleRequested: spec.ImageScaleFilter is not null,
            // Remux-only work copies encoded video frames unchanged, so a perceptual comparison
            // would add a full decode pass without providing another safety signal.
            VideoReencoded: spec.VideoCodec is not null);

        // Only a video re-encode needs a hardware/software encoder resolved. A non-null
        // VideoCodec is exactly the case the command builder re-encodes video for (audio,
        // image, and remux specs all leave it null) — gate on that rather than the file's
        // MediaKind, so a video classified Unknown still gets the selected GPU encoder
        // instead of silently falling back to the CPU library encoder.
        string? videoEncoderName = null;
        if (spec.VideoCodec is not null)
        {
            var videoEncoder = await ResolveVideoEncoderAsync(
                spec.VideoCodec,
                queueSettings.EncoderMode,
                cancellationToken);
            if (videoEncoder is { Succeeded: false })
            {
                throw new InvalidOperationException(videoEncoder.Error);
            }

            videoEncoderName = videoEncoder.EncoderName;
            logger.LogInformation(
                "Job {JobId} will encode video with '{Encoder}' (mode {Mode})",
                jobId, videoEncoderName, queueSettings.EncoderMode);
        }

        // The primary command honours the hardware-decode setting; the builder only applies it
        // when a hardware encoder is in use and no software tone-map is needed. When it does
        // apply, also build the software-decode equivalent so a source the GPU cannot decode can
        // be retried transparently rather than failing the job.
        var primaryArguments = FfmpegCommandBuilder.Build(
            spec, queueSettings.CpuThreadLimit, videoEncoderName, OptimisedMarkerValue, queueSettings.HardwareDecode);
        var softwareArguments = FfmpegCommandBuilder.Build(
            spec, queueSettings.CpuThreadLimit, videoEncoderName, OptimisedMarkerValue, hardwareDecode: false);
        var usedHardwareDecode = !primaryArguments.SequenceEqual(softwareArguments);

        return new JobWork(
            spec,
            primaryArguments,
            videoEncoderName,
            isPreview,
            media.DurationSeconds,
            library?.MoveOnComplete ?? false,
            library?.TargetFolder,
            library?.MoveOverwrite ?? false,
            media.Id,
            original,
            library?.MinVmafHarmonicMean,
            library?.MinVmafMin,
            library?.AutoReplace ?? false,
            usedHardwareDecode ? softwareArguments : null);
    }

    private sealed record FfmpegRun(int ExitCode, string? Error, string? Log);

    private async Task<FfmpegRun> RunFfmpegAsync(
        int jobId,
        IReadOnlyList<string> arguments,
        double? durationSeconds,
        bool hardwareEncoder,
        CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = transcodeOptions.Ffmpeg,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();

        // Make this ffmpeg visible to the metrics broadcaster so it can read the process's GPU
        // counters, and flag whether it uses a hardware encoder for the sidebar indicator.
        using var registration = encodes.Track(process.Id, hardwareEncoder);

        // Drain stdout so the pipe never blocks; progress and errors come on stderr.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = ReadStderrAsync(process, jobId, durationSeconds, cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            KillQuietly(process);
            throw;
        }

        await stdoutTask;
        var stderr = await stderrTask;
        return process.ExitCode == 0
            ? new FfmpegRun(process.ExitCode, null, null)
            : new FfmpegRun(process.ExitCode, stderr.Tail, stderr.Log);
    }

    private sealed record FfmpegStderr(string? Tail, string? Log);

    // Reads ffmpeg's stderr, pushing live progress/speed/ETA from its "time=" lines (throttled),
    // keeping the last few lines for a one-line failure message, and collecting the non-progress
    // lines into a bounded log so the full reason is recoverable from the API on failure.
    private async Task<FfmpegStderr> ReadStderrAsync(
        Process process,
        int jobId,
        double? durationSeconds,
        CancellationToken cancellationToken)
    {
        var tail = new Queue<string>();
        var log = new FfmpegLogBuffer();
        var lastReported = 0.0;

        string? line;
        while ((line = await process.StandardError.ReadLineAsync(cancellationToken)) is not null)
        {
            var sample = FfmpegProgressParser.Parse(line);
            var isProgress = sample.ElapsedSeconds is not null;

            // The progress frames are the bulk of stderr; keep only the substantive lines in the log.
            if (!isProgress)
            {
                log.Append(line);
            }

            tail.Enqueue(line);
            while (tail.Count > 12)
            {
                tail.Dequeue();
            }

            if (durationSeconds is not > 0 || sample.ElapsedSeconds is not { } elapsed)
            {
                continue;
            }

            var progress = Math.Clamp(elapsed / durationSeconds.Value, 0, 0.999);
            if (progress - lastReported < 0.01)
            {
                continue;
            }

            lastReported = progress;
            await UpdateProgressAsync(jobId, progress);
            var eta = sample.Speed is { } speed
                ? FfmpegProgressParser.EstimateRemainingSeconds(durationSeconds.Value, elapsed, speed)
                : null;
            await BroadcastProgressAsync(jobId, progress, sample.Fps, sample.Speed, eta);
        }

        return new FfmpegStderr(
            tail.Count > 0 ? string.Join('\n', tail) : null,
            log.ToLog());
    }

    private async Task BeginTranscodeAsync(
        int jobId, string outputPath, IReadOnlyList<string> arguments, string? videoEncoder, CancellationToken cancellationToken)
    {
        await WithJobAsync(jobId, job =>
        {
            job.WorkOutputPath = outputPath;
            job.FfmpegArguments = string.Join(' ', arguments);
            job.VideoEncoder = videoEncoder;
            job.UpdatedAt = DateTimeOffset.UtcNow;
        }, cancellationToken);
    }

    private Task UpdateProgressAsync(int jobId, double progress) =>
        WithJobAsync(jobId, job =>
        {
            job.Progress = progress;
            job.UpdatedAt = DateTimeOffset.UtcNow;
        }, CancellationToken.None);

    /// <summary>
    /// Number of terminal failures of a file's current version before it is auto-excluded. Surfaces
    /// on the library's Excluded tab and is fully reversible there.
    /// </summary>
    private const int AutoExcludeFailureThreshold = AutoExclusionPolicy.DefaultFailureThreshold;

    private async Task CompleteAsync(
        int jobId, JobStatus status, double? progress = null, string? error = null, string? processLog = null)
    {
        await _dbLock.WaitAsync(CancellationToken.None);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == jobId);
            if (job is null)
            {
                return;
            }

            job.Status = status;
            if (progress is { } value)
            {
                job.Progress = value;
            }
            if (error is not null)
            {
                job.ErrorMessage = error;
            }
            if (processLog is not null)
            {
                job.ProcessLog = processLog;
            }
            // Classify and store the reason once, the moment it fails, so the diagnostics summary can
            // group in the database and the class is stable even if the message is later edited.
            if (status == JobStatus.Failed)
            {
                job.FailureCategory = FailureClassifier.Classify(job.ErrorMessage);
            }
            job.FinishedAt = DateTimeOffset.UtcNow;
            job.UpdatedAt = DateTimeOffset.UtcNow;

            await ApplyFailureTrackingAsync(db, job, status);
            await db.SaveChangesAsync();
        }
        finally
        {
            _dbLock.Release();
        }
    }

    // Keep a durable per-file failure tally so a file that keeps failing is excluded automatically
    // (and shown on the Excluded tab) rather than offered forever; a successful encode clears the
    // streak. Excluding here never touches the original — it only stops the file being re-offered.
    // Internal so the pure DB effect can be unit tested without standing up the whole dispatcher.
    internal static async Task ApplyFailureTrackingAsync(OptimisarrDbContext db, Job job, JobStatus status)
    {
        var media = await db.MediaFiles.FirstOrDefaultAsync(f => f.Id == job.MediaFileId);
        if (media is null)
        {
            return;
        }

        if (status == JobStatus.Failed)
        {
            media.FailureCount += 1;
            media.UpdatedAt = DateTimeOffset.UtcNow;

            if (AutoExclusionPolicy.ShouldExclude(media.FailureCount, AutoExcludeFailureThreshold)
                && !await db.Exclusions.AnyAsync(e => e.Path == media.Path))
            {
                db.Exclusions.Add(new Exclusion
                {
                    Path = media.Path,
                    LibraryId = media.LibraryId,
                    RelativePath = media.RelativePath,
                    Reason = $"Auto-excluded after {media.FailureCount} failed attempts",
                    Source = ExclusionSource.RepeatedFailures
                });
            }
        }
        else if (status is JobStatus.Completed or JobStatus.ReadyToReplace && media.FailureCount != 0)
        {
            // The encode produced a verified output, so the failure streak is over.
            media.FailureCount = 0;
            media.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    // A clean ffmpeg exit only means the transcode ran; it does not mean the output
    // is sound. Verification is the gate to ReadyToReplace: a full-decode health
    // check plus duration/stream/size comparison against the original. A failed
    // report leaves the job Failed with the output retained for inspection — the
    // original is never touched either way.
    private async Task VerifyAndFinishAsync(int jobId, string outputPath, JobWork work, CancellationToken cancellationToken)
    {
        await WithJobAsync(jobId, job =>
        {
            job.Status = JobStatus.Verifying;
            job.UpdatedAt = DateTimeOffset.UtcNow;
        }, cancellationToken);
        await NotifyAsync();

        var settings = await GetQueueSettingsAsync(cancellationToken);
        var policy = VerificationPolicyResolver.Resolve(
            settings.VerificationPolicy, work.MinVmafHarmonicMean, work.MinVmafMin);
        var clip = work.IsPreview && work.Spec.ClipSeconds is { } seconds
            ? new VerificationClip(
                seconds,
                work.Spec.ClipStartSeconds,
                Path.Combine(Path.GetDirectoryName(outputPath)!, ".optimisarr-preview-reference.mkv"))
            : null;
        var outcome = await verification.VerifyAsync(
            work.Original, outputPath, policy, cancellationToken, clip);
        var reportJson = JsonSerializer.Serialize(outcome.Report, ReportJsonOptions);

        await WithJobAsync(jobId, job =>
        {
            job.OutputSizeBytes = outcome.OutputSizeBytes;
            job.VerificationReportJson = reportJson;
            job.VerificationPassed = outcome.Report.Passed;
            job.VerifiedAt = DateTimeOffset.UtcNow;
            job.UpdatedAt = DateTimeOffset.UtcNow;
        }, CancellationToken.None);

        if (!outcome.Report.Passed)
        {
            var failed = outcome.Report.Checks.Where(check => check.Outcome == CheckOutcome.Failed);
            var summary = "Verification failed: " + string.Join("; ", failed.Select(check => check.Name));
            await CompleteAsync(jobId, JobStatus.Failed, error: summary);
            await NotifyJobFailedAsync(jobId, summary);
            return;
        }

        if (work.IsPreview)
        {
            await CompleteAsync(jobId, JobStatus.Completed, progress: 1.0);
            return;
        }

        await FinishSuccessfulJobAsync(jobId, outputPath, work, settings.DryRunMode);
    }

    // On success the original is never touched. If the library collects outputs in a
    // target folder, move our work output there and mark the job Completed; otherwise
    // leave it in the work directory as ReadyToReplace (safe replacement is a later phase).
    private async Task FinishSuccessfulJobAsync(int jobId, string outputPath, JobWork work, bool dryRunMode)
    {
        if (work is { MoveOnComplete: true, TargetFolder: { } targetFolder })
        {
            // Resolve against this file's own work root so the per-media-file id segment is not
            // mirrored into the target folder — the destination keeps the library's structure.
            var mediaWorkRoot = WorkOutputRoot.ForMediaFile(_workRoot, work.MediaFileId);
            var destination = MoveTarget.Resolve(mediaWorkRoot, outputPath, targetFolder);

            // Don't silently clobber an existing converted file unless the library opts into it.
            // Failing here keeps the just-produced output in the work dir for inspection.
            if (!work.MoveOverwrite && File.Exists(destination))
            {
                var error = $"A converted file already exists at the destination and overwrite is off: {destination}";
                await CompleteAsync(jobId, JobStatus.Failed, progress: 1.0, error: error);
                await NotifyJobFailedAsync(jobId, error);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            MoveFile(outputPath, destination);
            // The output left the work dir; clean up its now-empty per-media scratch tree.
            WorkPaths.PruneEmptyAncestors(_workRoot, outputPath);

            await WithJobAsync(jobId, job =>
            {
                job.Status = JobStatus.Completed;
                job.Progress = 1.0;
                job.WorkOutputPath = destination;
                job.FinishedAt = DateTimeOffset.UtcNow;
                job.UpdatedAt = DateTimeOffset.UtcNow;
            }, CancellationToken.None);
            return;
        }

        await CompleteAsync(jobId, JobStatus.ReadyToReplace, progress: 1.0);

        // Hands-off replacement, when the library opts in and dry-run mode is off. The output
        // already passed every verification gate; ReplaceAsync still quarantines the original
        // first and records a rollback, so the safety model holds. A failure (e.g. an unwritable
        // folder) leaves the job ReadyToReplace for a manual retry rather than touching the original.
        if (work.AutoReplace && !dryRunMode)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var replacement = scope.ServiceProvider.GetRequiredService<ReplacementService>();
            var result = await replacement.ReplaceAsync(jobId, CancellationToken.None);
            if (result.Kind == ReplacementResultKind.Success)
            {
                logger.LogInformation("Job {JobId}: auto-replaced the original (library auto-replace).", jobId);
            }
            else if (result.Permanent)
            {
                // This replacement can never succeed (see ReplacementActionResult.Permanent), so fail
                // the job now rather than leaving it ReadyToReplace for the reconcile sweep to retry.
                await CompleteAsync(jobId, JobStatus.Failed, error: result.Message);
                logger.LogWarning(
                    "Job {JobId}: auto-replace cannot complete ({Kind}): {Message}. Marked Failed (will not retry).",
                    jobId, result.Kind, result.Message);
            }
            else
            {
                logger.LogWarning(
                    "Job {JobId}: auto-replace did not complete ({Kind}): {Message}. Left ReadyToReplace for manual replace.",
                    jobId, result.Kind, result.Message);
            }
            await NotifyAsync();
        }
    }

    // Move our own work output; never the original. Falls back to copy+delete across
    // filesystems, where an atomic rename isn't possible.
    private static void MoveFile(string source, string destination)
    {
        try
        {
            File.Move(source, destination, overwrite: true);
        }
        catch (IOException)
        {
            File.Copy(source, destination, overwrite: true);
            File.Delete(source);
        }
    }

    // The cancel endpoint already set the status to Cancelled; just tidy the output.
    private async Task HandleCancelledAsync(int jobId)
    {
        string? outputPath = null;
        await WithJobAsync(jobId, job =>
        {
            outputPath = job.WorkOutputPath;
            if (job.Status != JobStatus.Cancelled)
            {
                job.Status = JobStatus.Cancelled;
            }
            job.FinishedAt ??= DateTimeOffset.UtcNow;
            job.UpdatedAt = DateTimeOffset.UtcNow;
        }, CancellationToken.None);

        DeleteWorkOutput(outputPath);
    }

    private async Task WithJobAsync(int jobId, Action<Job> mutate, CancellationToken cancellationToken)
    {
        await _dbLock.WaitAsync(cancellationToken);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);
            if (job is null)
            {
                return;
            }

            mutate(job);
            await db.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async Task<QueueDispatchStatus> GetDispatchStatusAsync(CancellationToken cancellationToken)
    {
        var settings = await GetQueueSettingsAsync(cancellationToken);
        var activity = await activityMonitor.GetActivityAsync(cancellationToken);
        var decision = EvaluateDispatchPolicy(settings, activity);
        var freeDiskBytes = TryGetFreeDiskBytes(_workRoot);

        // When dispatch is otherwise ready but nothing runs, explain whether the backlog is just
        // waiting for closed per-library windows (the common "why isn't it running?" surprise).
        var waitingReason = decision.CanStart && _running.Count == 0
            ? await DescribeWindowWaitAsync(cancellationToken)
            : null;

        return new QueueDispatchStatus(
            decision.CanStart,
            decision.BlockedReason,
            _running.Count,
            settings.MaxConcurrentJobs,
            settings.MinFreeDiskBytes,
            settings.CpuThreadLimit,
            settings.EncoderMode,
            encodes.AnyHardware,
            freeDiskBytes,
            _workRoot,
            waitingReason);
    }

    private async Task<string?> DescribeWindowWaitAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();

        var queuedByLibrary = await db.Jobs
            .AsNoTracking()
            .Where(job => job.Status == JobStatus.Queued)
            .GroupBy(job => job.LibraryId)
            .Select(group => new { LibraryId = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        if (queuedByLibrary.Count == 0)
        {
            return null;
        }

        var libraries = (await db.Libraries
            .AsNoTracking()
            .Select(library => new
            {
                library.Id,
                library.Name,
                library.AutoEnqueueEnabled,
                library.AutoEnqueueWindowStart,
                library.AutoEnqueueWindowEnd,
            })
            .ToListAsync(cancellationToken))
            .ToDictionary(library => library.Id);

        var queues = queuedByLibrary.Select(entry =>
        {
            var library = entry.LibraryId is { } id && libraries.TryGetValue(id, out var match) ? match : null;
            var windowed = library is { AutoEnqueueEnabled: true };
            return new QueueWaitReason.LibraryQueue(
                library?.Name ?? "Unassigned",
                entry.Count,
                windowed ? library!.AutoEnqueueWindowStart : null,
                windowed ? library!.AutoEnqueueWindowEnd : null);
        }).ToList();

        return QueueWaitReason.Describe(queues, TimeOnly.FromDateTime(DateTime.Now));
    }

    /// <summary>
    /// Clears the pending queue to reset state (e.g. after a rules change): cancels anything in
    /// flight, then removes all Queued and ReadyToReplace jobs and discards their /work outputs.
    /// No original is ever touched — ReadyToReplace jobs hold only a verified, not-yet-applied
    /// output (no replacement, no rollback), so discarding them loses recomputable work, never
    /// data. Returns the number of jobs removed.
    /// </summary>
    public async Task<int> ClearPendingQueueAsync(CancellationToken cancellationToken)
    {
        // Stop in-flight jobs first; each running task observes cancellation, cleans up its partial
        // output, and finalises itself to Cancelled.
        foreach (var jobId in _running.Keys.ToList())
        {
            RequestCancel(jobId);
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();

        var pending = await db.Jobs
            .Where(job => job.Status == JobStatus.Queued || job.Status == JobStatus.ReadyToReplace)
            .ToListAsync(cancellationToken);
        if (pending.Count == 0)
        {
            return 0;
        }

        foreach (var job in pending)
        {
            DeleteWorkOutput(job.WorkOutputPath);
        }

        db.Jobs.RemoveRange(pending);
        await db.SaveChangesAsync(cancellationToken);
        await NotifyAsync();
        return pending.Count;
    }

    private async Task<QueueSettings> GetQueueSettingsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<SettingsStore>();
        return await settings.GetQueueSettingsAsync(cancellationToken);
    }

    private DispatchDecision EvaluateDispatchPolicy(QueueSettings settings, ActivityDecision activity) =>
        DispatchPolicyEvaluator.Evaluate(
            settings.MinFreeDiskBytes,
            TryGetFreeDiskBytes(_workRoot),
            activity.Active,
            activity.Reason);

    private async Task<EncoderSelection> ResolveVideoEncoderAsync(
        string? targetCodec,
        EncoderMode encoderMode,
        CancellationToken cancellationToken)
    {
        if (targetCodec is null)
        {
            return EncoderSelection.Success("copy");
        }

        var detected = await hardware.DetectAsync(cancellationToken);
        return EncoderSelector.Select(targetCodec, encoderMode, detected.Encoders);
    }

    // A hardware encoder is named after its API (e.g. hevc_qsv, h264_vaapi, hevc_nvenc); the
    // software libraries (libx265, libx264, libsvtav1) are not. Used to flag GPU-backed work.
    private static bool IsHardwareEncoder(string? encoder) =>
        encoder is not null
        && (encoder.EndsWith("_qsv", StringComparison.OrdinalIgnoreCase)
            || encoder.EndsWith("_vaapi", StringComparison.OrdinalIgnoreCase)
            || encoder.EndsWith("_nvenc", StringComparison.OrdinalIgnoreCase));

    private Task NotifyAsync() => hub.Clients.All.SendAsync("jobsChanged");

    // Best effort: tell configured notification targets a job failed. Resolves the
    // file path in its own scope and never lets a notification error escape.
    private async Task NotifyJobFailedAsync(int jobId, string error)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            var path = await db.Jobs
                .Where(job => job.Id == jobId && job.MediaFile != null)
                .Select(job => job.MediaFile!.Path)
                .FirstOrDefaultAsync();
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            var notifications = scope.ServiceProvider.GetRequiredService<NotificationService>();
            await notifications.NotifyFailureAsync(path, error, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failure notification for job {JobId} failed", jobId);
        }
    }

    // Live transcode telemetry. Sent as a lightweight payload (not persisted beyond
    // job.Progress) so the UI can move the bar and show speed/ETA without re-fetching.
    private Task BroadcastProgressAsync(int jobId, double progress, double? fps, double? speed, double? etaSeconds) =>
        hub.Clients.All.SendAsync("jobProgress", new { jobId, progress, fps, speed, etaSeconds });

    private void DeleteWorkOutput(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Could not delete work output {Path}", path);
        }

        // Tidy the per-media-file scratch directory this output lived in so /work does not
        // accumulate an empty tree for every file ever processed.
        WorkPaths.PruneEmptyAncestors(_workRoot, path);
    }

    private void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not kill ffmpeg process");
        }
    }

    private static long? TryGetFreeDiskBytes(string path)
    {
        try
        {
            var target = Directory.Exists(path)
                ? path
                : Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(target))
            {
                return null;
            }

            var root = Path.GetPathRoot(Path.GetFullPath(target));
            return string.IsNullOrWhiteSpace(root)
                ? null
                : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>The ffmpeg binary used for transcoding (see OPTIMISARR_FFMPEG).</summary>
public sealed record TranscodeOptions(string Ffmpeg);

public sealed record QueueDispatchStatus(
    bool CanStart,
    string? BlockedReason,
    int RunningJobs,
    int MaxConcurrentJobs,
    long MinFreeDiskBytes,
    int CpuThreadLimit,
    EncoderMode EncoderMode,
    bool HardwareAccelerated,
    long? FreeDiskBytes,
    string WorkRoot,
    // Set when dispatch is ready but nothing starts because every queued job's library window is
    // shut (e.g. "1605 job(s) waiting for the TV optimise window (00:00–05:00)"). Null otherwise.
    string? WaitingReason);
