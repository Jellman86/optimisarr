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
    ILogger<QueueDispatcher> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
    private const int MaxAttempts = 3;

    private static readonly JsonSerializerOptions ReportJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _workRoot = ResolveWorkRoot(environment);
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
        await RecoverInterruptedJobsAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchAsync(stoppingToken);
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
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            queued = await db.Jobs
                .AsNoTracking()
                .Where(job => job.Status == JobStatus.Queued)
                .Select(job => new QueuedJob(job.Id, job.LibraryId, job.Priority, job.EnqueuedAt))
                .ToListAsync(stoppingToken);
        }

        var toStart = JobScheduler.SelectJobsToStart(queued, _running.Count, maxConcurrent);
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

            var (spec, arguments) = work.Value;
            Directory.CreateDirectory(Path.GetDirectoryName(spec.OutputPath)!);
            await BeginTranscodeAsync(jobId, spec.OutputPath, arguments, cancellationToken);
            await NotifyAsync();

            var run = await RunFfmpegAsync(jobId, arguments, work.Value.DurationSeconds, cancellationToken);

            if (run.ExitCode == 0)
            {
                await VerifyAndFinishAsync(jobId, spec.OutputPath, work.Value, cancellationToken);
            }
            else
            {
                DeleteWorkOutput(spec.OutputPath);
                var error = run.Error ?? $"ffmpeg exited with code {run.ExitCode}";
                await CompleteAsync(jobId, JobStatus.Failed, error: error);
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
        double? DurationSeconds,
        bool MoveOnComplete,
        string? TargetFolder,
        OriginalSnapshot Original)
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

        var spec = TranscodeSpecResolver.Resolve(
            rules,
            media.Path,
            media.RelativePath,
            _workRoot,
            media.IsHdr,
            library?.QualityCrf,
            library?.EncoderPreset);

        var original = new OriginalSnapshot(
            media.SizeBytes,
            media.DurationSeconds,
            media.AudioTrackCount ?? 0,
            media.SubtitleTrackCount ?? 0);

        var videoEncoder = await ResolveVideoEncoderAsync(
            rules.TargetVideoCodec,
            queueSettings.EncoderMode,
            cancellationToken);
        if (videoEncoder is { Succeeded: false })
        {
            throw new InvalidOperationException(videoEncoder.Error);
        }

        return new JobWork(
            spec,
            FfmpegCommandBuilder.Build(spec, queueSettings.CpuThreadLimit, videoEncoder.EncoderName),
            media.DurationSeconds,
            library?.MoveOnComplete ?? false,
            library?.TargetFolder,
            original);
    }

    private sealed record FfmpegRun(int ExitCode, string? Error);

    private async Task<FfmpegRun> RunFfmpegAsync(
        int jobId,
        IReadOnlyList<string> arguments,
        double? durationSeconds,
        CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
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
        var tail = await stderrTask;
        return new FfmpegRun(process.ExitCode, process.ExitCode == 0 ? null : tail);
    }

    // Reads ffmpeg's stderr, pushing live progress/speed/ETA from its "time=" lines
    // (throttled) and keeping the last few lines for a useful failure message.
    private async Task<string?> ReadStderrAsync(
        Process process,
        int jobId,
        double? durationSeconds,
        CancellationToken cancellationToken)
    {
        var tail = new Queue<string>();
        var lastReported = 0.0;

        string? line;
        while ((line = await process.StandardError.ReadLineAsync(cancellationToken)) is not null)
        {
            tail.Enqueue(line);
            while (tail.Count > 12)
            {
                tail.Dequeue();
            }

            if (durationSeconds is not > 0)
            {
                continue;
            }

            var sample = FfmpegProgressParser.Parse(line);
            if (sample.ElapsedSeconds is not { } elapsed)
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

        return tail.Count > 0 ? string.Join('\n', tail) : null;
    }

    private async Task BeginTranscodeAsync(int jobId, string outputPath, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        await WithJobAsync(jobId, job =>
        {
            job.WorkOutputPath = outputPath;
            job.FfmpegArguments = string.Join(' ', arguments);
            job.UpdatedAt = DateTimeOffset.UtcNow;
        }, cancellationToken);
    }

    private Task UpdateProgressAsync(int jobId, double progress) =>
        WithJobAsync(jobId, job =>
        {
            job.Progress = progress;
            job.UpdatedAt = DateTimeOffset.UtcNow;
        }, CancellationToken.None);

    private Task CompleteAsync(int jobId, JobStatus status, double? progress = null, string? error = null) =>
        WithJobAsync(jobId, job =>
        {
            job.Status = status;
            if (progress is { } value)
            {
                job.Progress = value;
            }
            if (error is not null)
            {
                job.ErrorMessage = error;
            }
            job.FinishedAt = DateTimeOffset.UtcNow;
            job.UpdatedAt = DateTimeOffset.UtcNow;
        }, CancellationToken.None);

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
        var outcome = await verification.VerifyAsync(
            work.Original, outputPath, settings.VerificationPolicy, cancellationToken);
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

        await FinishSuccessfulJobAsync(jobId, outputPath, work);
    }

    // On success the original is never touched. If the library collects outputs in a
    // target folder, move our work output there and mark the job Completed; otherwise
    // leave it in the work directory as ReadyToReplace (safe replacement is a later phase).
    private async Task FinishSuccessfulJobAsync(int jobId, string outputPath, JobWork work)
    {
        if (work is { MoveOnComplete: true, TargetFolder: { } targetFolder })
        {
            var destination = MoveTarget.Resolve(_workRoot, outputPath, targetFolder);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            MoveFile(outputPath, destination);

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

        return new QueueDispatchStatus(
            decision.CanStart,
            decision.BlockedReason,
            _running.Count,
            settings.MaxConcurrentJobs,
            settings.ScheduleEnabled,
            settings.ScheduleWindowStart,
            settings.ScheduleWindowEnd,
            settings.MinFreeDiskBytes,
            settings.CpuThreadLimit,
            settings.EncoderMode,
            freeDiskBytes,
            _workRoot);
    }

    private async Task<QueueSettings> GetQueueSettingsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<SettingsStore>();
        return await settings.GetQueueSettingsAsync(cancellationToken);
    }

    private DispatchDecision EvaluateDispatchPolicy(QueueSettings settings, ActivityDecision activity) =>
        DispatchPolicyEvaluator.Evaluate(
            settings.ScheduleEnabled,
            settings.ScheduleWindowStart,
            settings.ScheduleWindowEnd,
            TimeOnly.FromDateTime(DateTime.Now),
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

    private static string ResolveWorkRoot(IHostEnvironment environment)
    {
        var configured = Environment.GetEnvironmentVariable("OPTIMISARR_WORK_DIR");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        return Directory.Exists("/work")
            ? "/work"
            : Path.Combine(environment.ContentRootPath, "work");
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

public sealed record QueueDispatchStatus(
    bool CanStart,
    string? BlockedReason,
    int RunningJobs,
    int MaxConcurrentJobs,
    bool ScheduleEnabled,
    TimeOnly ScheduleWindowStart,
    TimeOnly ScheduleWindowEnd,
    long MinFreeDiskBytes,
    int CpuThreadLimit,
    EncoderMode EncoderMode,
    long? FreeDiskBytes,
    string WorkRoot);
