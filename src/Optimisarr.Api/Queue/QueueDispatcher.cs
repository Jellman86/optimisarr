using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Library;
using Optimisarr.Api.Realtime;
using Optimisarr.Core.Queue;
using Optimisarr.Data;

namespace Optimisarr.Api.Queue;

/// <summary>
/// The transcode worker. A single background loop owns all job-state transitions
/// (SQLite has one writer), selecting work via the pure <see cref="JobScheduler"/>
/// and running ffmpeg out-of-process. A job only ever writes to the work directory;
/// it never deletes or overwrites the original — safe replacement is a later phase.
/// </summary>
public sealed partial class QueueDispatcher(
    IServiceScopeFactory scopeFactory,
    IHubContext<JobsHub> hub,
    IHostEnvironment environment,
    ILogger<QueueDispatcher> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
    private const int MaxAttempts = 3;

    private readonly string _workRoot = ResolveWorkRoot(environment);
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _running = new();
    private readonly SemaphoreSlim _dbLock = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);

    [GeneratedRegex(@"time=\s*(\d+):(\d{2}):(\d{2}(?:\.\d+)?)")]
    private static partial Regex ProgressTime();

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
        var maxConcurrent = await GetMaxConcurrentJobsAsync(stoppingToken);
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
                await CompleteAsync(jobId, JobStatus.ReadyToReplace, progress: 1.0);
            }
            else
            {
                DeleteWorkOutput(spec.OutputPath);
                await CompleteAsync(jobId, JobStatus.Failed, error: run.Error ?? $"ffmpeg exited with code {run.ExitCode}");
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

    private readonly record struct JobWork(TranscodeSpec Spec, IReadOnlyList<string> Arguments, double? DurationSeconds)
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

        var spec = TranscodeSpecResolver.Resolve(
            rules,
            media.Path,
            media.RelativePath,
            _workRoot,
            media.IsHdr,
            library?.QualityCrf,
            library?.EncoderPreset);

        return new JobWork(spec, FfmpegCommandBuilder.Build(spec), media.DurationSeconds);
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

    // Reads ffmpeg's stderr, updating progress from "time=" lines (throttled) and
    // keeping the last few lines for a useful failure message.
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

            if (durationSeconds is > 0 && TryParseElapsed(line, out var elapsed))
            {
                var progress = Math.Clamp(elapsed / durationSeconds.Value, 0, 0.999);
                if (progress - lastReported >= 0.01)
                {
                    lastReported = progress;
                    await UpdateProgressAsync(jobId, progress);
                    await NotifyAsync();
                }
            }
        }

        return tail.Count > 0 ? string.Join('\n', tail) : null;
    }

    private static bool TryParseElapsed(string line, out double seconds)
    {
        seconds = 0;
        var match = ProgressTime().Match(line);
        if (!match.Success)
        {
            return false;
        }

        var hours = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var minutes = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var secs = double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        seconds = (hours * 3600) + (minutes * 60) + secs;
        return true;
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

    private async Task<int> GetMaxConcurrentJobsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<SettingsStore>();
        return await settings.GetMaxConcurrentJobsAsync(cancellationToken);
    }

    private Task NotifyAsync() => hub.Clients.All.SendAsync("jobsChanged");

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
}
