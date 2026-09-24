using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Library;
using Optimisarr.Api.Realtime;
using Optimisarr.Api.Replacement;
using Optimisarr.Api.Workers;
using Optimisarr.Core.Activity;
using Optimisarr.Core.Library;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Scheduling;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Tools;
using Optimisarr.Core.Verification;
using Optimisarr.Core.Workers;
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
    QueuePauseManager pauseManager,
    ImageMarkerService imageMarker,
    ImageComparisonReferenceService imageReference,
    TranscodeOptions transcodeOptions,
    ActiveEncodeRegistry encodes,
    ILogger<QueueDispatcher> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan OrphanWorkGracePeriod = TimeSpan.FromDays(7);
    private const int MaxAttempts = 3;

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
    private sealed record ActiveWork(CancellationTokenSource Cancellation, WorkloadLane Lane);
    private readonly ConcurrentDictionary<int, ActiveWork> _running = new();
    private int? _lastStartedLibraryId;
    private WorkloadLane? _lastStartedVideoClass;
    private WorkloadSlots RunningSlots() => new(
        _running.Values.Count(work => work.Lane == WorkloadLane.Video),
        _running.Values.Count(work => work.Lane == WorkloadLane.NonVideo),
        _running.Values.Count(work => work.Lane == WorkloadLane.Evidence));
    // Scratch directories an encode is writing into. A directory is momentarily empty between
    // being created and FFmpeg opening its output, and two jobs on the same media file share one
    // directory — so without this, one job's cleanup prunes another job's freshly made tree.
    // When each queued job first became eligible to run, so a PreferWorker hold measures the time a
    // worker actually had to claim it rather than time the job spent parked outside its window.
    private readonly ConcurrentDictionary<int, DateTimeOffset> _firstRunnableAt = new();
    // Only ever touched from the single dispatch loop, so no lock is needed for these two.
    private string? _lastIdleSummary;
    private DateTimeOffset _lastIdleLoggedAt = DateTimeOffset.MinValue;
    private readonly ConcurrentDictionary<string, int> _reservedWorkDirectories =
        new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _dbLock = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private int _draining;

    /// <summary>Nudges the loop to dispatch immediately (e.g. right after enqueue).</summary>
    public void Wake()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { /* already signalled */ }
    }

    /// <summary>Stops the ffmpeg process backing a running job, if any.</summary>
    public void RequestCancel(int jobId)
    {
        if (_running.TryGetValue(jobId, out var work))
        {
            work.Cancellation.Cancel();
        }
    }

    /// <summary>
    /// Stops accepting work, then lets active transcodes and verification passes finish within the
    /// host's shutdown budget. If the orchestrator exhausts that budget, cancel the remaining work
    /// so normal crash recovery can safely re-queue it on the next start.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _draining, 1);
        Wake();
        logger.LogInformation(
            "Queue is draining {Count} active job(s) before shutdown; no new jobs will start.",
            _running.Count);

        var released = await pauseManager.ReleaseProcessesForShutdownAsync(CancellationToken.None);
        if (!released.Resumed)
        {
            logger.LogWarning(
                "Could not resume {Count} suspended encode(s) for shutdown drain; cancelling active jobs for safe recovery.",
                released.FailedEncodeCount);
            foreach (var source in _running.Values)
            {
                source.Cancellation.Cancel();
            }
        }

        try
        {
            while (!_running.IsEmpty && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // The bounded host shutdown window elapsed; cancel below.
        }

        if (!_running.IsEmpty)
        {
            logger.LogWarning(
                "Shutdown drain window elapsed; cancelling {Count} active job(s) for safe recovery.",
                _running.Count);
            foreach (var source in _running.Values)
            {
                source.Cancellation.Cancel();
            }
        }

        await base.StopAsync(CancellationToken.None);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await pauseManager.RestoreAsync(stoppingToken);
        await PurgeDisposableJobsAsync(stoppingToken);
        await RecoverInterruptedJobsAsync(stoppingToken);
        await PurgeAbandonedWorkAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchAsync(stoppingToken);
                // Apply "Replace automatically" retrospectively: jobs already in ReadyToReplace when
                // the toggle was turned on (or left there by a transient replace failure) are picked
                // up here, not just jobs that verify after the toggle. A manual pause holds this
                // sweep too — its bulk file moves are exactly the load the operator paused to avoid.
                if (!pauseManager.IsPaused)
                {
                    await ReconcileAutoReplaceAsync(stoppingToken);
                }
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

    // Interactive media is throwaway and must not survive a restart. Failed comparison rows are a
    // small diagnostic audit, however, so retain those while removing every scratch tree.
    private async Task PurgeDisposableJobsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();

        var disposable = await db.Jobs
            .Where(job => job.Type != JobType.Normal)
            .ToListAsync(cancellationToken);
        var discard = disposable
            .Where(job => !DiagnosticJobRetention.ShouldRetain(job.Type, job.Status))
            .ToList();
        if (discard.Count > 0)
        {
            db.Jobs.RemoveRange(discard);
            await db.SaveChangesAsync(cancellationToken);
        }

        foreach (var subtree in new[] { "preview", "calibration" })
        {
            var root = $"{_workRoot.TrimEnd('/', '\\')}/{subtree}";
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "Could not purge disposable work directory {Path}", root);
            }
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
            job.Progress = 0;
            job.UpdatedAt = DateTimeOffset.UtcNow;

            switch (RecoveryActionFor(job.Status, job.WorkOutputPath, job.Attempt))
            {
                case RecoveryAction.KeepDelivered:
                    // The interrupted work was this machine's verification of a candidate a remote
                    // worker delivered. The candidate is finished work, not a half-written encode;
                    // it goes back to waiting and verification simply runs again.
                    job.Status = JobStatus.AwaitingVerification;
                    break;
                case RecoveryAction.Fail:
                    DeleteWorkOutput(job.WorkOutputPath);
                    job.WorkOutputPath = null;
                    job.Status = JobStatus.Failed;
                    job.ErrorMessage = "Interrupted too many times (worker restarted while running).";
                    job.FinishedAt = DateTimeOffset.UtcNow;
                    break;
                default:
                    DeleteWorkOutput(job.WorkOutputPath);
                    job.WorkOutputPath = null;
                    job.Status = JobStatus.Queued;
                    break;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Recovered {Count} interrupted job(s) after restart", interrupted.Count);
        await NotifyAsync();
    }

    internal enum RecoveryAction { Requeue, Fail, KeepDelivered }

    /// <summary>
    /// What restart recovery does with a job found mid-flight. A local encode or verification that
    /// was interrupted has nothing worth keeping and is requeued (or failed once it has been
    /// interrupted too often); a delivered remote candidate whose verification was interrupted is
    /// kept, because the expensive part is done and only this machine's verdict is missing.
    /// </summary>
    internal static RecoveryAction RecoveryActionFor(JobStatus status, string? workOutputPath, int attempt)
    {
        if (status == JobStatus.Verifying && RemoteCandidate.IsDelivered(workOutputPath))
        {
            return RecoveryAction.KeepDelivered;
        }

        return attempt >= MaxAttempts ? RecoveryAction.Fail : RecoveryAction.Requeue;
    }

    // Scratch output is deliberately retained while a failed job exists so the operator can inspect
    // it. Once a row has been removed, however, an old numeric work directory has no owner and would
    // otherwise consume disk forever. Cancelled jobs never need an output, so tidy those immediately;
    // orphan directories receive a seven-day grace period to avoid racing any external inspection.
    private async Task PurgeAbandonedWorkAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();

        var cancelled = await db.Jobs
            .Where(job => job.Status == JobStatus.Cancelled && job.WorkOutputPath != null)
            .ToListAsync(cancellationToken);
        foreach (var job in cancelled)
        {
            if (TryDiscardWorkOutput(job.WorkOutputPath))
            {
                job.WorkOutputPath = null;
                job.OutputSizeBytes = null;
                job.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }
        if (cancelled.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        var referencedMediaFileIds = (await db.Jobs
                .Where(job => job.WorkOutputPath != null)
                .Select(job => job.MediaFileId)
                .Distinct()
                .ToListAsync(cancellationToken))
            .ToHashSet();
        var cutoff = DateTime.UtcNow - OrphanWorkGracePeriod;
        foreach (var directory in WorkPaths.FindStaleOrphanDirectories(
                     _workRoot, referencedMediaFileIds, cutoff))
        {
            try
            {
                Directory.Delete(directory, recursive: true);
                logger.LogInformation("Removed stale orphan work directory {Directory}", directory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Could not remove orphan work directory {Directory}", directory);
            }
        }
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
                    queueSettings.DryRunMode,
                    pauseManager.IsPaused))
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
            var guarded = await pauseManager.TryRunAutomaticActionAsync(async () =>
            {
                await using var replaceScope = scopeFactory.CreateAsyncScope();
                var replacement = replaceScope.ServiceProvider.GetRequiredService<ReplacementService>();
                return await replacement.ReplaceAsync(jobId, cancellationToken);
            }, cancellationToken);
            if (!guarded.Started)
            {
                break;
            }

            var result = guarded.Value!;
            if (result.Kind == ReplacementResultKind.Success)
            {
                replaced++;
                logger.LogInformation("Job {JobId}: auto-replaced the original (library auto-replace, reconciled).", jobId);
            }
            else if (result.Kind == ReplacementResultKind.AlreadyCompleted)
            {
                logger.LogDebug(
                    "Job {JobId}: auto-replace reconciliation observed a replacement already completed by another caller.",
                    jobId);
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
            else if (!AutoReplacePolicy.IsFault(result.Kind))
            {
                // A library rule declined this one on purpose — the file is still hardlinked. It
                // stays ReadyToReplace and will go through once that is no longer true, so this is
                // a state to be able to look up, not a fault to be told about every three seconds.
                logger.LogDebug(
                    "Job {JobId}: auto-replace deferred by a library rule: {Message}. Left ReadyToReplace.",
                    jobId, result.Message);
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
        if (Volatile.Read(ref _draining) != 0)
        {
            return;
        }

        var settings = await GetQueueSettingsAsync(stoppingToken);
        var limits = settings.EffectiveWorkloadSlots(Environment.ProcessorCount,
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);

        List<QueuedJob> queued;
        HashSet<int> adaptiveLibraryIds = [];
        List<(int Id, WorkloadLane Lane)> delivered;
        Dictionary<int, (TimeOnly Start, TimeOnly End)> autoWindows;
        var remoteWorkersOn = false;
        var aWorkerCouldTakeWork = false;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            // A library's placement only means something while work can actually go elsewhere:
            // the switch on and the preview flag present. Otherwise "only on workers" would hold a
            // job for a claim that the worker routes refuse, which is a stall nobody asked for.
            var availability = await WorkerAvailability.ResolveAsync(
                db,
                settings.RemoteWorkersEnabled,
                scope.ServiceProvider.GetRequiredService<RemoteWorkersFeature>(),
                DateTimeOffset.UtcNow,
                stoppingToken);
            remoteWorkersOn = availability.RemoteWorkersOn;
            aWorkerCouldTakeWork = availability.AWorkerCouldTakeWork;

            var placements = await db.Libraries
                .AsNoTracking()
                .Select(library => new { library.Id, library.WorkPlacement })
                .ToDictionaryAsync(library => library.Id, library => library.WorkPlacement, stoppingToken);
            // Libraries whose jobs a worker cannot be offered until this machine has chosen a
            // per-title quality for them.
            var adaptiveLibraries = await db.Libraries
                .AsNoTracking()
                .Where(library => library.VideoQualityStrategy == VideoQualityStrategy.AdaptiveVmaf)
                .Select(library => library.Id)
                .ToListAsync(stoppingToken);
            adaptiveLibraryIds = [.. adaptiveLibraries];
            delivered = await DeliveredWorkloadsAsync(db, stoppingToken);
            queued = (await db.Jobs
                .AsNoTracking()
                .Where(job => job.Status == JobStatus.Queued)
                .Select(job => new
                {
                    job.Id,
                    job.LibraryId,
                    job.Priority,
                    job.EnqueuedAt,
                    Kind = job.MediaFile != null ? job.MediaFile.MediaKind : MediaKind.Unknown,
                    IgnoreMediaActivity = job.Type == JobType.Calibration && job.IgnoreMediaActivity,
                    IgnoreLibraryWindow = job.Type == JobType.Preview,
                    QualityChosen = job.AdaptiveVideoQuality != null,
                    // Only a normal job is ever offered to a worker, so only a normal job can be
                    // held for one; a calibration or preview is placed exactly as before.
                    HonoursPlacement = job.Type == JobType.Normal,
                })
                .ToListAsync(stoppingToken))
                .Select(job => new QueuedJob(
                    job.Id,
                    job.LibraryId,
                    job.Priority,
                    job.EnqueuedAt,
                    job.IgnoreMediaActivity,
                    job.IgnoreLibraryWindow,
                    job.HonoursPlacement && job.LibraryId is { } libraryId
                        && placements.TryGetValue(libraryId, out var placement)
                        ? placement
                        : WorkPlacement.Anywhere,
                    job.QualityChosen,
                    job.Kind))
                .ToList();

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

        var activity = await activityMonitor.GetActivityAsync(stoppingToken);
        var hasActivityBypass = queued.Any(job => job.IgnoreMediaActivity);
        var policy = EvaluateDispatchPolicy(settings, activity, hasActivityBypass);
        if (!policy.CanStart)
        {
            logger.LogDebug("Queue dispatch paused: {Reason}", policy.BlockedReason);
            return;
        }

        // Strict sidecar evidence uses a small independent lane: it must not wait for a long
        // container encode. Legacy delivered candidates still require full media verification
        // here and therefore take video capacity.
        var running = RunningSlots();
        foreach (var (jobId, lane) in delivered)
        {
            if (Volatile.Read(ref _draining) != 0
                || running.For(lane) >= limits.For(lane)
                || _running.ContainsKey(jobId)
                || !await TryClaimDeliveredAsync(jobId, stoppingToken))
            {
                continue;
            }

            var cts = new CancellationTokenSource();
            _running[jobId] = new ActiveWork(cts, lane);
            running = running.WithStarted(lane);
            _ = Task.Run(() => VerifyDeliveredAsync(jobId, cts.Token), CancellationToken.None);
        }

        var nowLocal = TimeOnly.FromDateTime(DateTime.Now);
        var nowUtc = DateTimeOffset.UtcNow;

        // The window first, then placement — the order matters, and getting it the other way round
        // is why "prefer a worker" quietly never preferred one. PreferWorker gives a worker first
        // refusal for a few minutes, and that clock used to run from when the job was enqueued. A
        // library with an optimise window enqueues its work hours before that window opens, so the
        // hold expired while every job sat ineligible to run at all; by the time the window opened
        // the server was free to take the lot, and did. The hold now starts when a job first
        // becomes runnable, which is what it was always meant to mean.
        var withinWindow = JobScheduler.WithinLibraryWindows(queued, autoWindows, nowLocal);

        // Kept in memory rather than on the job: losing it across a restart simply restarts the
        // hold, which errs towards offering the work to a worker — the safe direction for a
        // setting whose whole purpose is to prefer one.
        foreach (var job in withinWindow)
        {
            _firstRunnableAt.TryAdd(job.Id, nowUtc);
        }
        PruneFirstRunnable(queued);

        // Every job a worker could take is now held for one, including those whose per-title
        // quality is still unchosen: the search travels with the job, so a worker can be offered it
        // before a quality exists. That was not true yesterday, and the exception carved out for it
        // then would now hand every adaptive job straight back to this machine — the exact stall it
        // was written to cure, inverted.
        var runnable = SelectLocallyRunnable(
            withinWindow,
            _firstRunnableAt,
            remoteWorkersOn,
            _ => aWorkerCouldTakeWork,
            nowUtc);

        var toStart = JobScheduler.SelectJobsByWorkload(runnable, running, limits,
            mediaServicesActive: activity.Active, lastStartedLibraryId: _lastStartedLibraryId,
            lastStartedVideoClass: _lastStartedVideoClass);

        // Say why nothing started, when something plainly could have.
        //
        // Every gate above is individually reasonable and none of them logged, so a queue holding
        // ninety jobs with nothing running, nothing paused and no waiting reason on the status
        // endpoint was indistinguishable from a dispatcher that had simply stopped. Diagnosing it
        // from outside took three wrong guesses; naming the filter that emptied the list makes it
        // a fact instead. Logged once per cycle at Information, and only when the answer is
        // genuinely surprising — queued work, free capacity, and still nothing chosen.
        if (toStart.Count == 0 && queued.Count > 0 && runnable.Any(job =>
                running.Video < limits.Video ||
                (JobScheduler.LaneFor(job.Kind) == WorkloadLane.NonVideo && running.NonVideo < limits.NonVideo)))
        {
            // Throttled, because the loop runs every three seconds: a queue parked outside its
            // window overnight would otherwise write some thirty thousand identical lines and bury
            // everything worth reading. Logged when the answer changes, and once every five minutes
            // besides so a long stall still leaves a trail rather than one line at the start of it.
            var summary = $"{queued.Count}/{withinWindow.Count}/{runnable.Count}/{activity.Active}";
            var now = DateTimeOffset.UtcNow;
            if (summary != _lastIdleSummary || now - _lastIdleLoggedAt > TimeSpan.FromMinutes(5))
            {
                _lastIdleSummary = summary;
                _lastIdleLoggedAt = now;
                logger.LogInformation(
                "Queue: {Queued} queued, none started — {InWindow} inside their library window, "
                + "{Runnable} of those this machine may run ({Placement}), "
                + "{Startable} selected (media activity: {Activity})",
                queued.Count,
                withinWindow.Count,
                runnable.Count,
                withinWindow.Count == runnable.Count
                    ? "placement is not holding any back"
                    : $"{withinWindow.Count - runnable.Count} held for a worker",
                toStart.Count,
                activity.Active ? "streaming" : "idle");
            }
        }

        foreach (var (jobId, lane) in toStart)
        {
            if (Volatile.Read(ref _draining) != 0
                || _running.ContainsKey(jobId)
                || !await TryClaimAsync(jobId, stoppingToken))
            {
                continue;
            }

            var cts = new CancellationTokenSource();
            _running[jobId] = new ActiveWork(cts, lane);
            _lastStartedLibraryId = queued.First(job => job.Id == jobId).LibraryId;
            if (lane == WorkloadLane.Video)
                _lastStartedVideoClass = JobScheduler.LaneFor(queued.First(job => job.Id == jobId).Kind);
            _ = Task.Run(() => RunJobAsync(jobId, cts.Token), CancellationToken.None);
        }
    }

    internal static async Task<List<(int Id, WorkloadLane Lane)>> DeliveredWorkloadsAsync(
        OptimisarrDbContext db, CancellationToken cancellationToken)
    {
        var ids = await db.Jobs.AsNoTracking()
            .Where(job => job.Status == JobStatus.AwaitingVerification)
            .OrderBy(job => job.Id)
            .Select(job => job.Id)
            .ToListAsync(cancellationToken);
        if (ids.Count == 0) return [];

        var leases = await db.JobLeases.AsNoTracking()
            .Where(lease => ids.Contains(lease.JobId) && lease.State == LeaseState.Completed)
            .Select(lease => new { lease.JobId, lease.AcquiredAt,
                HasStrictEvidence = lease.VerificationContractJson != null })
            .ToListAsync(cancellationToken);
        var latest = leases.GroupBy(lease => lease.JobId).ToDictionary(group => group.Key,
            group => group.OrderByDescending(lease => lease.AcquiredAt).First().HasStrictEvidence);
        return ids.Select(id => (id,
            latest.GetValueOrDefault(id) ? WorkloadLane.Evidence : WorkloadLane.Video)).ToList();
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

            PrepareForAttempt(job, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        finally
        {
            _dbLock.Release();
        }
    }

    /// <summary>
    /// The worker whose completed lease delivered this job's candidate — the latest one, should a
    /// job ever have been delivered more than once. Ordered in memory: SQLite cannot ORDER BY a
    /// DateTimeOffset, which the first live delivery proved by failing here. A job holds at most a
    /// handful of leases, so loading them is cheap.
    /// </summary>
    internal static async Task<Worker?> DeliveringWorkerAsync(
        OptimisarrDbContext db,
        int jobId,
        CancellationToken cancellationToken) =>
        (await DeliveringLeaseAsync(db, jobId, cancellationToken))?.Worker;

    /// <summary>The lease under which the candidate now on disk was delivered: the latest completed one.</summary>
    internal static async Task<JobLease?> DeliveringLeaseAsync(
        OptimisarrDbContext db,
        int jobId,
        CancellationToken cancellationToken)
    {
        var completed = await db.JobLeases
            .AsNoTracking()
            .Include(lease => lease.Worker)
            .Where(lease => lease.JobId == jobId && lease.State == LeaseState.Completed)
            .ToListAsync(cancellationToken);

        return completed
            .OrderByDescending(lease => lease.AcquiredAt)
            .FirstOrDefault();
    }

    private async Task RecordWorkerProblemAsync(int workerId, string message, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var worker = await db.Workers.FirstOrDefaultAsync(w => w.Id == workerId, cancellationToken);
        if (worker is null)
        {
            return;
        }

        WorkerProblems.Record(worker, message, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
    }

    // Single-writer claim for a delivered candidate: only transition if still waiting, so two
    // dispatch cycles can never verify the same candidate at once.
    private async Task<bool> TryClaimDeliveredAsync(int jobId, CancellationToken cancellationToken)
    {
        await _dbLock.WaitAsync(cancellationToken);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);
            if (job is null || job.Status != JobStatus.AwaitingVerification)
            {
                return false;
            }

            var now = DateTimeOffset.UtcNow;
            job.Status = JobStatus.Verifying;
            job.Progress = 0;
            job.StartedAt ??= now;
            job.UpdatedAt = now;
            job.ErrorMessage = null;
            job.FailureCategory = null;
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        finally
        {
            _dbLock.Release();
        }
    }

    /// <summary>
    /// Judges a delivered candidate against its lease snapshot. Strict sidecar results validate
    /// bound evidence without repeating media checks; older leases run the full server verification
    /// path. Neither route can replace a source until every required gate passes.
    /// </summary>
    private async Task VerifyDeliveredAsync(int jobId, CancellationToken cancellationToken)
    {
        try
        {
            string? candidatePath;
            string? sourceSha256;
            JobLease? deliveredLease;
            await using (var scope = scopeFactory.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
                var facts = await db.Jobs
                    .AsNoTracking()
                    .Where(job => job.Id == jobId)
                    .Select(job => new { job.WorkOutputPath, job.SourceSha256 })
                    .FirstOrDefaultAsync(cancellationToken);
                candidatePath = facts?.WorkOutputPath;
                sourceSha256 = facts?.SourceSha256;
                deliveredLease = await DeliveringLeaseAsync(db, jobId, cancellationToken);
            }

            var deliveredBy = deliveredLease?.Worker;

            if (candidatePath is null || !File.Exists(candidatePath))
            {
                await CompleteAsync(jobId, JobStatus.Failed,
                    error: "The delivered candidate is missing from the work directory, so it cannot be verified.");
                return;
            }

            if (deliveredBy is null)
            {
                await CompleteAsync(jobId, JobStatus.Failed,
                    error: "No completed lease records which worker delivered this candidate, so its encode contract cannot be rebuilt.");
                return;
            }

            // The executed encode plan, including its colour conversion, belongs to the lease.
            // A later settings edit or a different worker must not change the verification target.
            // Existing leases without a snapshot cannot be reconstructed safely after settings
            // or worker capabilities change. Keep their candidate and original for inspection.
            if (deliveredLease!.VerificationWorkJson is not { } frozen)
            {
                throw new InvalidOperationException(
                    "The worker assignment's frozen verification plan is missing. The original was kept; retry this job to create a new assignment.");
            }
            var work = JsonSerializer.Deserialize<JobWork?>(frozen, ReportJsonOptions);
            if (work is null)
            {
                await CompleteAsync(jobId, JobStatus.Failed, error: "Job or media file no longer exists.");
                return;
            }

            await using (var policyScope = scopeFactory.CreateAsyncScope())
            {
                var db = policyScope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
                var library = await db.Libraries.AsNoTracking()
                    .Where(library => db.Jobs.Any(job => job.Id == jobId && job.LibraryId == library.Id))
                    .SingleOrDefaultAsync(cancellationToken);
                work = work.Value with
                {
                    // The worker's byte limit and verification contract were fixed together at
                    // claim time. Applying a later library edit here could reject an output the
                    // worker was told to produce, or accept one the worker was told to stop.
                    AutoReplace = library?.AutoReplace ?? false,
                    MoveOnComplete = library?.MoveOnComplete ?? false,
                    TargetFolder = library?.TargetFolder,
                    MoveOverwrite = library?.MoveOverwrite ?? false
                };
            }

            if (!File.Exists(work.Value.Original.Path))
            {
                await CompleteAsync(jobId, JobStatus.Failed, error:
                    $"Source file no longer exists: {work.Value.Original.Path}. It was most likely moved or "
                    + "upgraded by your media manager (Radarr/Sonarr). Re-scan the library and the stale entry "
                    + "will be removed.");
                return;
            }

            await WithJobAsync(jobId, job => job.VideoEncoder = work.Value.VideoEncoder, cancellationToken);

            // The worker's own VMAF measurement stands in for this machine's only when it is bound
            // to these exact bytes and this policy. Anything less is measured again here, and the
            // worker's card says why its evidence was not taken.
            var delivered = DeliveredQualityEvidence.Resolve(deliveredLease!, sourceSha256, work.Value.VerificationPolicy);
            RemoteVerificationEvidence? fullEvidence = null;
            if (deliveredLease!.VerificationContractJson is { } verificationJson)
            {
                var contract = JsonSerializer.Deserialize<RemoteVerificationContract>(verificationJson, ReportJsonOptions)
                    ?? throw new InvalidOperationException("The full verification contract is missing.");
                fullEvidence = deliveredLease.VerificationEvidenceJson is { } evidenceJson
                    ? JsonSerializer.Deserialize<RemoteVerificationEvidence>(evidenceJson, ReportJsonOptions) : null;
                var objections = RemoteVerificationEvidenceValidator.Validate(
                    contract, fullEvidence, sourceSha256, deliveredLease.DeliveredSha256);
                if (objections.Count > 0)
                    throw new InvalidOperationException("Sidecar-only verification failed: " + string.Join(" ", objections));
                if (work.Value.VerificationPolicy.RequiresVmaf(work.Value.Spec.Kind, work.Value.Original.VideoReencoded)
                    && delivered.Accepted is null)
                    throw new InvalidOperationException("Sidecar-only verification requires valid worker VMAF evidence. Server fallback is disabled.");
            }
            if (delivered.WasAsked && delivered.Accepted is null
                && work.Value.VerificationPolicy.RequiresVmaf(work.Value.Spec.Kind, work.Value.Original.VideoReencoded))
            {
                // The worker's card keeps only its latest problem, and a later verdict overwrites
                // this one within minutes. The log is where the reason survives.
                logger.LogWarning(
                    "Job {JobId}: quality evidence from {Worker} was not accepted, measuring VMAF locally: {Objections}",
                    jobId, deliveredBy.Name,
                    delivered.Objections.Count > 0 ? string.Join(" ", delivered.Objections) : "no evidence was returned");
                await RecordWorkerProblemAsync(
                    deliveredBy.Id,
                    $"Its quality evidence for {Path.GetFileName(work.Value.Original.Path)} was not accepted, so this server measured VMAF itself: "
                    + (delivered.Objections.Count > 0 ? string.Join(" ", delivered.Objections) : "no evidence was returned."),
                    cancellationToken);
            }

            if (delivered.Accepted is not null)
            {
                logger.LogInformation(
                    "Job {JobId}: quality evidence from {Worker} accepted; VMAF will not be re-measured here",
                    jobId, deliveredBy.Name);
            }
            var disposition = await VerifyAndFinishAsync(
                jobId, candidatePath, work.Value, cancellationToken,
                // A worker's hardware decode can corrupt frames as a local one can; the retry
                // cannot happen on the worker, so it is a requeue with software decode required.
                softwareDecodeRetryAvailable: deliveredLease!.HardwareDecoder is not null,
                remoteQuality: delivered.Accepted,
                remoteEvidence: fullEvidence);
            if (disposition == VerificationDisposition.RetryWithSoftwareDecode)
            {
                DeleteWorkOutput(candidatePath);
                await WithJobAsync(jobId, job =>
                {
                    JobAttemptHistory.RequeueAfterRejectedCandidate(
                        job, deliveredBy.Name, deliveredLease.HardwareDecoder, DateTimeOffset.UtcNow);
                }, cancellationToken);
                logger.LogWarning(
                    "Job {JobId}: the candidate {Worker} decoded with {Decoder} showed decoder corruption; requeued to be encoded with software decode",
                    jobId, deliveredBy.Name, deliveredLease.HardwareDecoder);
                await RecordWorkerProblemAsync(
                    deliveredBy.Id,
                    $"Its {deliveredLease.HardwareDecoder} decode of {Path.GetFileName(work.Value.Original.Path)} produced corrupt frames; the job was requeued to decode in software.",
                    cancellationToken);
                return;
            }

            await RecordDeliveredVerdictAsync(jobId, deliveredBy.Id, cancellationToken);
        }
        catch (JobNoLongerEligibleException ex)
        {
            await CompleteAsync(jobId, JobStatus.Cancelled, error: $"Skipped before verifying: {ex.Message}");
            logger.LogInformation("Job {JobId}: skipped before verifying — {Reason}", jobId, ex.Message);
        }
        catch (OperationCanceledException)
        {
            await HandleCancelledAsync(jobId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Job {JobId} failed while verifying a delivered candidate", jobId);
            await CompleteAsync(jobId, JobStatus.Failed, error: ex.Message);
            await NotifyJobFailedAsync(jobId, ex.Message);
        }
        finally
        {
            if (_running.TryRemove(jobId, out var work))
            {
                work.Cancellation.Dispose();
            }
            await NotifyAsync();
            Wake();
        }
    }

    /// <summary>
    /// A delivered candidate that failed verification is the worker's problem to hear about, on
    /// its own card: the worker itself only ever learns that its upload was accepted.
    /// </summary>
    private async Task RecordDeliveredVerdictAsync(int jobId, int workerId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var verdict = await db.Jobs
            .AsNoTracking()
            .Where(job => job.Id == jobId)
            .Select(job => new { job.Status, job.ErrorMessage, Path = job.MediaFile != null ? job.MediaFile.RelativePath : null })
            .FirstOrDefaultAsync(cancellationToken);
        if (verdict is null || verdict.Status != JobStatus.Failed)
        {
            return;
        }

        var worker = await db.Workers.FirstOrDefaultAsync(w => w.Id == workerId, cancellationToken);
        if (worker is null)
        {
            return;
        }

        WorkerProblems.Record(
            worker,
            $"Its candidate for {verdict.Path ?? $"job {jobId}"} failed verification: {verdict.ErrorMessage ?? "no reason recorded"}.",
            DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Whether a job whose per-title quality has just been chosen should go back to the queue for a
    /// worker rather than being encoded here.
    ///
    /// <para>Only when a worker could actually take it. A preference that stalled a library because
    /// nothing was listening would be worse than ignoring the preference, so an absent, drained or
    /// incapable fleet means this machine simply carries on.</para>
    /// </summary>
    /// <summary>
    /// Expresses one candidate quality as commands the worker holding this job can run.
    ///
    /// <para>Built here rather than in the endpoint because it needs the job's spec, its encoder and
    /// the source's picture — the same facts <see cref="PrepareRemoteWorkAsync"/> assembles to build
    /// an assignment. The endpoint asks for a candidate and gets commands; it never learns what a
    /// sample encode looks like.</para>
    ///
    /// <para>Null when the search cannot be expressed at all: no quality gate, no readable source
    /// picture, no windows. The caller then leaves the job to be searched locally, which is the
    /// behaviour that existed before any of this.</para>
    /// </summary>
    public async Task<AdaptiveSearchStep?> PlanAdaptiveStepAsync(
        int jobId,
        int quality,
        WorkerCapabilities worker,
        CancellationToken cancellationToken)
    {
        // Loaded for the worker that will run it, never for this machine. Resolving the encoder
        // from this server's probe would plan the search on the wrong encoder entirely — the exact
        // mistake moving the search was meant to end — and on a server whose probe offers nothing
        // for the target codec it throws instead, which is how a test caught it.
        JobWork? work;
        try
        {
            work = await LoadWorkAsync(jobId, new EncodePlacement(worker), cancellationToken);
        }
        catch (InvalidOperationException)
        {
            // No encoder this worker can use for the target codec. The caller refuses the job,
            // which leaves it to be searched and encoded here.
            return null;
        }

        if (work is not { } loaded
            || loaded.Spec.VideoCodec is null
            || loaded.VideoEncoder is null
            || loaded.SourcePicture is not { } source)
        {
            return null;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        // The interface, not the concrete service. Both resolve to the same singleton in the
        // running app; asking for the abstraction is what lets the offered search be proven in a
        // test, which is the half of this feature that was left unproven because it could not be.
        var sourceProbe = await scope.ServiceProvider
            .GetRequiredService<IMediaProbeService>()
            .ProbeAsync(loaded.Original.Path, cancellationToken);

        // The primary picture timeline, not the container's. A subtitle or attachment stream can
        // extend a container well past the programme, and a window planned on that can land after
        // the last frame and produce an empty, unscorable sample.
        var samplingDuration = MediaTimelineDuration.Resolve(
            MediaKind.Video,
            sourceProbe.VideoDurationSeconds,
            loaded.DurationSeconds);
        if (samplingDuration is not > 0)
        {
            return null;
        }

        return AdaptiveSearchPlanner.Plan(
            quality,
            loaded.Spec,
            loaded.VideoEncoder,
            Path.GetExtension(loaded.Spec.OutputPath),
            VmafWindowPlanner.PlanAdaptive(samplingDuration.Value),
            loaded.VerificationPolicy,
            source.Width,
            source.Height,
            loaded.Original.IsHdr,
            loaded.Original.HdrConvertedToSdr,
            samplingDuration,
            // The job's own rate is empty on this path; the probe just made is the authority, as
            // it is for the final measurement. Without a rate neither side gets a common grid.
            loaded.Spec.TargetFrameRate ?? loaded.VideoFrameRate ?? sourceProbe.VideoFrameRate,
            ContainerLeadSeconds(sourceProbe),
            loaded.Spec.CropTo,
            loaded.Spec.FrameRate);
    }

    /// <summary>
    /// The facts a worker quality-search exchange needs about a job: what its evidence is
    /// judged by, which quality its encoder searches around, and the source-size policy.
    ///
    /// <para>These come from the job's work resolved for that worker. Rebuilding an approximate
    /// policy from the contract's two thresholds would leave out the catastrophic floor and judge
    /// a remote search more leniently. Taking the requested quality instead of the worker's effective
    /// one would bracket around a different number than the assignment planned.</para>
    /// </summary>
    public async Task<(VerificationPolicy Policy, int Baseline, bool BypassSizePreflight)?> GetSearchContextAsync(
        int jobId, WorkerCapabilities worker, CancellationToken cancellationToken)
    {
        // Use the same encoder placement that planned the sample commands. A worker may support
        // HEVC even when this server has no local HEVC encoder, and quality scales differ.
        var work = await LoadWorkAsync(jobId, new EncodePlacement(worker), cancellationToken);
        return work is { VideoQuality: { } quality }
            ? (work.Value.VerificationPolicy, quality.Effective, work.Value.BypassSizePreflight)
            : null;
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
            if (!work.Value.IsDisposable)
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

            var preparedWork = work.Value;
            if (ShouldSelectAdaptiveQuality(
                preparedWork.VideoQualityStrategy,
                preparedWork.Spec.VideoCodec is not null,
                preparedWork.VideoQuality is not null,
                preparedWork.AdaptiveVideoQuality is not null,
                preparedWork.IsCalibration))
            {
                var selection = await SelectAdaptiveQualityAsync(jobId, preparedWork, cancellationToken);
                if (selection is null)
                {
                    // The sample-size forecast is advisory, so this job waits for an operator
                    // rather than producing or failing a candidate. It owns no full output.
                    return;
                }
                preparedWork = selection.Value;
                // The sample result belongs to this encoder. Worker-first placement already had
                // its head start before this machine claimed the job; handing the chosen quality
                // to a different encoder would discard the proof the search just gathered.
            }

            var (spec, arguments) = preparedWork;
            var hardwareEncoder = IsHardwareEncoder(work.Value.VideoEncoder);
            var maxCandidateBytes = SizeBudget.MaxCandidateBytes(
                preparedWork.Original.SizeBytes,
                preparedWork.VerificationPolicy.RequireSizeReduction,
                preparedWork.IsDisposable,
                spec.VideoCodec is not null
                    ? preparedWork.VerificationPolicy.MinimumSizeSavingPercent : null);
            var minCandidateBytes = SizeBudget.MinCandidateBytes(
                preparedWork.Original.SizeBytes,
                preparedWork.VerificationPolicy.RequireSizeReduction,
                preparedWork.IsDisposable,
                spec.VideoCodec is not null
                    ? preparedWork.VerificationPolicy.MaximumSizeSavingPercent : null);
            await BeginTranscodeAsync(
                jobId,
                spec.OutputPath,
                arguments,
                preparedWork.VideoEncoder,
                preparedWork.VideoQuality,
                cancellationToken);
            await NotifyAsync();

            // A clipped preview only encodes the clip window, so progress is measured against that,
            // not the full runtime (otherwise the bar would barely move then jump to done).
            var progressDuration = spec.ClipSeconds is { } clip && (work.Value.DurationSeconds is not { } d || clip < d)
                ? clip
                : work.Value.DurationSeconds;
            // A frame-rate cap emits fewer frames than the source holds; progress is counted in
            // frames emitted, so the estimate is scaled to what survives the decimation.
            var expectedFrameCount = FrameRatePlanner.ScaleFrameCount(
                FfmpegProgressCalculator.ExpectedFramesForWindow(
                    work.Value.FrameCount,
                    work.Value.DurationSeconds,
                    progressDuration,
                    work.Value.VideoFrameRate),
                work.Value.VideoFrameRate,
                spec.TargetFrameRate);
            var run = await RunFfmpegAsync(
                jobId,
                spec.OutputPath,
                arguments,
                progressDuration,
                expectedFrameCount,
                hardwareEncoder,
                cancellationToken,
                maxCandidateBytes: maxCandidateBytes,
                minCandidateBytes: minCandidateBytes);

            // Hardware decode and hardware tone-map support vary by source, driver, and FFmpeg
            // build. Retry a recognised setup failure once with the established software path.
            if (run.ExitCode != 0
                && !run.SizeGateFailed
                && preparedWork.SoftwareFallbackArguments is { } softwareArguments
                && (preparedWork.UsedHardwareDecode
                        && HardwareDecodeFallback.ShouldRetryInSoftware(run.Log ?? run.Error)
                    || preparedWork.UsedHardwareToneMap
                        && HardwareToneMapFallback.ShouldRetryInSoftware(run.Log ?? run.Error)))
            {
                logger.LogWarning(
                    "Job {JobId}: hardware decode or tone mapping failed; retrying with the software path. ffmpeg: {Error}",
                    jobId, run.Error);
                DeleteWorkOutput(spec.OutputPath);
                arguments = softwareArguments;
                await BeginTranscodeAsync(
                    jobId,
                    spec.OutputPath,
                    arguments,
                    preparedWork.VideoEncoder,
                    preparedWork.VideoQuality,
                    cancellationToken);
                run = await RunFfmpegAsync(
                    jobId,
                    spec.OutputPath,
                    arguments,
                    progressDuration,
                    expectedFrameCount,
                    hardwareEncoder,
                    cancellationToken,
                    maxCandidateBytes: maxCandidateBytes,
                    minCandidateBytes: minCandidateBytes);
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

                    if (work.Value.IsCalibration)
                    {
                        var referencePath = Path.Combine(
                            Path.GetDirectoryName(spec.OutputPath)!,
                            ".optimisarr-comparison-reference.png");
                        var reference = await imageReference.CreateAsync(
                            work.Value.Original.Path,
                            referencePath,
                            cancellationToken);
                        if (!reference.Created)
                        {
                            throw new InvalidOperationException(
                                $"Could not create the lossless image comparison reference: {reference.Error}");
                        }

                        if (!await imageMarker.CopyMetadataAsync(
                                work.Value.Original.Path,
                                referencePath,
                                cancellationToken))
                        {
                            logger.LogWarning(
                                "Job {JobId}: could not copy source colour metadata to the PNG comparison reference.",
                                jobId);
                        }
                    }
                }

                var disposition = await VerifyAndFinishAsync(
                    jobId,
                    spec.OutputPath,
                    preparedWork,
                    cancellationToken,
                    softwareDecodeRetryAvailable: preparedWork.UsedHardwareDecode
                        && preparedWork.SoftwareFallbackArguments is not null);
                if (disposition == VerificationDisposition.RetryWithSoftwareDecode)
                {
                    // One more encode, with the universal software decoder feeding the same encoder.
                    // The retried work cannot fall back again, so this cannot loop.
                    DeleteWorkOutput(spec.OutputPath);
                    arguments = preparedWork.SoftwareFallbackArguments!;
                    var retriedWork = preparedWork with
                    {
                        Arguments = arguments,
                        SoftwareFallbackArguments = null,
                        UsedHardwareDecode = false,
                        UsedHardwareToneMap = false,
                        SoftwareDecodeRetryReason = HardwareDecodeFallback.SoftwareDecodeRetryReason
                    };
                    await WithJobAsync(jobId, job =>
                    {
                        job.Status = JobStatus.Transcoding;
                        job.Progress = 0;
                        job.VerificationPassed = null;
                        job.VerifiedAt = null;
                        job.OutputSizeBytes = null;
                        job.UpdatedAt = DateTimeOffset.UtcNow;
                    }, cancellationToken);
                    await BeginTranscodeAsync(
                        jobId,
                        spec.OutputPath,
                        arguments,
                        preparedWork.VideoEncoder,
                        preparedWork.VideoQuality,
                        cancellationToken);
                    await NotifyAsync();
                    run = await RunFfmpegAsync(
                        jobId,
                        spec.OutputPath,
                        arguments,
                        progressDuration,
                        expectedFrameCount,
                        hardwareEncoder,
                        cancellationToken,
                        maxCandidateBytes: maxCandidateBytes,
                        minCandidateBytes: minCandidateBytes);
                    if (run.ExitCode == 0)
                    {
                        await VerifyAndFinishAsync(jobId, spec.OutputPath, retriedWork, cancellationToken);
                    }
                    else
                    {
                        await FailFromFfmpegRunAsync(jobId, spec.OutputPath, run);
                    }
                }
            }
            else
            {
                await FailFromFfmpegRunAsync(jobId, spec.OutputPath, run);
            }
        }
        catch (JobNoLongerEligibleException ex)
        {
            await CompleteAsync(jobId, JobStatus.Cancelled, error: $"Skipped before encoding: {ex.Message}");
            logger.LogInformation("Job {JobId}: skipped before encoding — {Reason}", jobId, ex.Message);
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
            if (_running.TryRemove(jobId, out var work))
            {
                work.Cancellation.Dispose();
            }
            await NotifyAsync();
            Wake(); // a slot just freed up
        }
    }

    internal static bool ShouldSelectAdaptiveQuality(
        VideoQualityStrategy strategy,
        bool hasVideoCodec,
        bool hasVideoQuality,
        bool hasAdaptiveQuality,
        bool isCalibration) =>
        strategy == VideoQualityStrategy.AdaptiveVmaf
        && hasVideoCodec
        && hasVideoQuality
        && !hasAdaptiveQuality
        // Calibration already prepares three exact source scenes for each concrete preset. Running
        // a second early/middle/late search inside every scene both multiplies the work and changes
        // the quality being compared. Its normal clip verification still measures VMAF after encode.
        && !isCalibration;

    // This snapshot is persisted on a strict sidecar lease and deserialized after delivery. Keep
    // the nested type visible to System.Text.Json so a restart cannot lose the frozen assignment.
    internal readonly record struct JobWork(
        TranscodeSpec Spec,
        IReadOnlyList<string> Arguments,
        string? VideoEncoder,
        EncoderQuality? VideoQuality,
        bool IsDisposable,
        bool IsCalibration,
        double? DurationSeconds,
        int? FrameCount,
        double? VideoFrameRate,
        bool MoveOnComplete,
        string? TargetFolder,
        bool MoveOverwrite,
        int MediaFileId,
        OriginalSnapshot Original,
        VerificationPolicy VerificationPolicy,
        VideoQualityStrategy VideoQualityStrategy,
        WorkPlacement Placement,
        int? AdaptiveVideoQuality,
        bool AutoReplace,
        int CpuThreadLimit,
        bool HardwareDecodeRequested,
        bool HardwareToneMapRequested,
        // Equivalent command using software decode and the established software tone-map. It is
        // present only when the primary command used a hardware path that can safely fall back.
        IReadOnlyList<string>? SoftwareFallbackArguments = null,
        bool UsedHardwareDecode = false,
        bool UsedHardwareToneMap = false,
        // The inventory's picture size, when it has one. A remote assignment names the VMAF
        // model from it so the worker's evidence can be held to the model this machine would use.
        PictureSize? SourcePicture = null,
        // Why this work is a second encode of the same job, when it is one; carried into the
        // verification report's context so the record explains itself.
        string? SoftwareDecodeRetryReason = null,
        bool BypassSizePreflight = false)
    {
        public void Deconstruct(out TranscodeSpec spec, out IReadOnlyList<string> arguments)
        {
            spec = Spec;
            arguments = Arguments;
        }
    }

    private static VerificationPolicy ResolveVerificationPolicy(
        VerificationPolicy baseline,
        Optimisarr.Data.Library? library) =>
        VerificationPolicyResolver.Resolve(
            baseline,
            new VerificationPolicyOverrides(
                library?.VmafQualityGateEnabled,
                library?.MinVmafHarmonicMean,
                library?.MinVmafMin,
                library?.MinVmafCatastrophicMin,
                library?.ClipVmafEnabled,
                library?.VmafFrameSubsample,
                library?.DurationTolerancePercent,
                library?.RequireAudioRetained,
                library?.RequireSubtitlesRetained,
                library?.RequireSizeReduction,
                library?.AudioLoudnessGateEnabled,
                library?.MaxLoudnessDriftLufs,
                library?.AudioClippingGateEnabled,
                library?.MaxTruePeakDbtp,
                library?.ImageQualityGateEnabled,
                library?.MinimumImageSsim,
                library?.ImageMetadataGateEnabled,
                library?.MinimumSizeSavingPercent,
                library?.MaximumSizeSavingPercent));

    /// <summary>
    /// Resolves one queued job into an assignment a remote worker could execute, or a reason it
    /// cannot. Runs the same preparation as local dispatch — fresh probes, crop detection, the
    /// picture and audio contract, verification policy — with the encoder chosen from the worker's
    /// proved capabilities and the command made portable. A refusal is ordinary and never throws.
    /// </summary>
    public async Task<RemoteWorkPlan> PrepareRemoteWorkAsync(
        int jobId,
        WorkerCapabilities worker,
        CancellationToken cancellationToken,
        bool forceStrictVerification = false)
    {
        JobWork? prepared;
        try
        {
            prepared = await LoadWorkAsync(jobId, new EncodePlacement(worker), cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or JobNoLongerEligibleException)
        {
            return RemoteWorkPlan.Refused(ex.Message);
        }

        if (prepared is not { } work)
        {
            return RemoteWorkPlan.Refused("The job or its media file no longer exists.");
        }

        if (!WorkPlacementPolicy.MayRunOnWorker(work.Placement))
        {
            return RemoteWorkPlan.Refused("The library keeps its work on this server.");
        }

        var strictVerification = forceStrictVerification || (await GetQueueSettingsAsync(cancellationToken)).WorkerVerificationRequired;
        if (strictVerification && work.IsDisposable)
            return RemoteWorkPlan.Refused("Sidecar-only verification requires a full-file video job.");

        // Only a video re-encode has an encoder to match and arguments worth shipping; a remux,
        // audio or image job is cheap enough that distributing it buys nothing yet.
        if (work.Spec.VideoCodec is null || work.VideoEncoder is null)
        {
            return RemoteWorkPlan.Refused("Only video re-encodes are offered to remote workers.");
        }

        // Adaptive selection runs sample encodes on an encoder, and a quality proven on one means
        // nothing on another — which is exactly why the search now travels with the job rather than
        // being done here first. The worker measures the candidates this machine chooses, on the
        // encoder that will do the real encode.
        AdaptiveSearchStep? search = null;
        if (work.VideoQualityStrategy == VideoQualityStrategy.AdaptiveVmaf && work.AdaptiveVideoQuality is null)
        {
            search = work.VideoQuality is { } baseline
                ? await PlanAdaptiveStepAsync(jobId, baseline.Effective, worker, cancellationToken)
                : null;

            // A search that cannot be expressed as commands — no quality gate, no readable source
            // picture, no windows — keeps the job here, where the local search can still run. That
            // is the behaviour this feature replaced, kept as its fallback.
            if (search is null)
            {
                return RemoteWorkPlan.Refused(
                    "A per-title quality search could not be planned for this job, so it stays on this server.");
            }
        }

        var (width, height) = work.SourcePicture is { } picture
            ? (work.Spec.CropTo?.Width ?? picture.Width, work.Spec.CropTo?.Height ?? picture.Height)
            : (0, 0);

        // The measurement seeks on the source's frame grid, which needs to know where its first
        // picture sits relative to its container start. That is not kept on the media record, so
        // the source is probed here; a failed probe only costs the grid alignment, not the plan.
        double? referenceContainerLead = null;
        var referenceFrameRate = work.Spec.TargetFrameRate ?? work.VideoFrameRate;
        if (work.SourcePicture is not null && work.VerificationPolicy.QualityGateEnabled)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var sourceProbe = await scope.ServiceProvider
                .GetRequiredService<MediaProbeService>()
                .ProbeAsync(work.Original.Path, cancellationToken);
            referenceContainerLead = ContainerLeadSeconds(sourceProbe);
            referenceFrameRate ??= sourceProbe.VideoFrameRate;
        }
        var quality = work.SourcePicture is { } source
            ? RemoteQualityPlanner.Plan(
                work.VerificationPolicy,
                source.Width,
                source.Height,
                work.Original.IsHdr,
                work.Original.HdrConvertedToSdr,
                work.DurationSeconds,
                referenceFrameRate,
                referenceContainerLead,
                work.Spec.CropTo,
                work.Spec.FrameRate)
            : null;

        return RemoteWorkPlan.For(new RemoteAssignment(
            work.VideoEncoder,
            work.Arguments,
            Path.GetExtension(work.Spec.OutputPath).TrimStart('.'),
            work.VerificationPolicy,
            QualityScoreCommandBuilder.ModelVersionFor(width, height),
            quality,
            work.UsedHardwareDecode ? RemoteHardwareDecoder(worker, work.VideoEncoder) : null,
            work.Spec.AudioEncoder,
            search,
            strictVerification ? new RemoteVerificationContract(1, Guid.NewGuid(),
                work.VerificationPolicy.AudioLoudnessGateEnabled || work.VerificationPolicy.AudioClippingGateEnabled) : null,
            JsonSerializer.Serialize(work, ReportJsonOptions),
            work.VideoQuality?.Requested,
            work.VideoQuality?.Effective,
            work.VideoQuality?.Mode));
    }

    /// <summary>
    /// Where an encode will run. Local work resolves its encoder from this machine's probe and may
    /// use its hardware decoder; remote work resolves it from the worker's proved capabilities and
    /// receives a portable command with placeholder paths.
    /// </summary>
    private readonly record struct EncodePlacement(WorkerCapabilities? RemoteWorker)
    {
        public static EncodePlacement Local => default;

        public bool IsRemote => RemoteWorker is not null;
    }

    private Task<JobWork?> LoadWorkAsync(int jobId, CancellationToken cancellationToken) =>
        LoadWorkAsync(jobId, EncodePlacement.Local, cancellationToken);

    private async Task<JobWork?> LoadWorkAsync(
        int jobId,
        EncodePlacement placement,
        CancellationToken cancellationToken)
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
        var isCalibration = job.Type == JobType.Calibration;
        var isDisposable = isPreview || isCalibration;
        if (isCalibration && (job.CalibrationSessionId is null
            || media.MediaKind == MediaKind.Video
                && (job.RequestedVideoQuality is null || job.RequestedRuleProfile is null)
            || media.MediaKind == MediaKind.Audio && job.RequestedAudioBitrateKbps is null
            || media.MediaKind == MediaKind.Image && job.RequestedImageQuality is null))
        {
            throw new InvalidOperationException("Calibration job is missing its session or requested quality.");
        }
        if (isCalibration && media.MediaKind == MediaKind.Video)
        {
            rules = LibraryRuleResolution.ResolveVideoPreset(library!, job.RequestedRuleProfile!.Value);
        }
        else if (isCalibration && media.MediaKind == MediaKind.Audio)
        {
            rules = rules with { AudioBitrateKbps = job.RequestedAudioBitrateKbps!.Value };
        }
        else if (isCalibration && media.MediaKind == MediaKind.Image)
        {
            rules = rules with { ImageQuality = job.RequestedImageQuality!.Value };
        }

        var isVideoJob = media.MediaKind is not (MediaKind.Audio or MediaKind.Image);

        // Language-based removal is destructive, so the inventory cache is never the authority at
        // dispatch time. Fresh-probe every source governed by either language rule and fail before
        // FFmpeg if that proof cannot be gathered. This also closes the same-size/same-mtime edge
        // case where a file was replaced without invalidating its cached track order.
        var needsSubtitleProbe = isVideoJob
            && (media.SubtitleTrackCount ?? 0) > 0
            && TranscodeSpecResolver.IsMp4Container(rules.TargetContainer);
        var sourceAudioLanguages = TrackLanguages.ParseTrackLanguages(media.AudioLanguages);
        var needsLanguageProbe = isVideoJob && rules.KeepAudioLanguages.Count > 0;
        var sourceSubtitleLanguages = TrackLanguages.ParseTrackLanguages(media.SubtitleLanguages);
        var needsSubtitleLanguageProbe = isVideoJob && rules.KeepSubtitleLanguages.Count > 0;
        var sourceHasImageSubtitles = false;
        MediaProbeResult? freshSourceProbe = null;
        if (needsSubtitleProbe || needsLanguageProbe || needsSubtitleLanguageProbe)
        {
            var probe = scope.ServiceProvider.GetRequiredService<MediaProbeService>();
            freshSourceProbe = await probe.ProbeAsync(media.Path, cancellationToken);
            if (!freshSourceProbe.Success)
            {
                throw new InvalidOperationException(
                    $"Fresh source probe required for safe track selection failed: {freshSourceProbe.Error ?? "no detail available"}");
            }

            sourceHasImageSubtitles = freshSourceProbe.HasImageSubtitles;
            if (needsLanguageProbe)
            {
                sourceAudioLanguages = freshSourceProbe.AudioTracks.Select(track => track.Language).ToList();
            }
            if (needsSubtitleLanguageProbe)
            {
                sourceSubtitleLanguages = freshSourceProbe.SubtitleLanguages;
            }
        }
        if (isPreview && isVideoJob)
        {
            freshSourceProbe ??= await scope.ServiceProvider
                .GetRequiredService<MediaProbeService>()
                .ProbeAsync(media.Path, cancellationToken);
            if (!freshSourceProbe.Success)
            {
                throw new InvalidOperationException(
                    $"Fresh source probe required for an exact preview timeline failed: {freshSourceProbe.Error ?? "no detail available"}");
            }

            sourceHasImageSubtitles = freshSourceProbe.HasImageSubtitles;
        }
        // A frame-rate cap is planned from the source's average rate, which the inventory does not
        // keep. The fresh probe is authoritative; without one the cap would have to be guessed,
        // and a guessed rate decimates the wrong frames, so the job stops here instead.
        if (isVideoJob && rules.MaxFrameRate is not null && rules.TargetVideoCodec is not null)
        {
            freshSourceProbe ??= await scope.ServiceProvider
                .GetRequiredService<MediaProbeService>()
                .ProbeAsync(media.Path, cancellationToken);
            if (!freshSourceProbe.Success)
            {
                throw new InvalidOperationException(
                    $"Fresh source probe required to apply the frame-rate cap failed: {freshSourceProbe.Error ?? "no detail available"}");
            }
        }
        var mediaDurationSeconds = isPreview && isVideoJob
            ? MediaTimelineDuration.Resolve(
                MediaKind.Video,
                freshSourceProbe!.VideoDurationSeconds,
                freshSourceProbe.DurationSeconds ?? media.DurationSeconds)
            : media.DurationSeconds;
        if (isPreview && isVideoJob && mediaDurationSeconds is not > 0)
        {
            throw new InvalidOperationException(
                "Could not determine the selected video's primary picture duration for Preview.");
        }

        // MP4/MOV has no tag for some Blu-ray audio (TrueHD, LPCM); copying one into an MP4 target
        // aborts the encode. The inventory already recorded the source's audio codecs, so this needs
        // no extra probe — the resolver falls back to MKV when such audio would be copied.
        var sourceHasMp4IncompatibleAudio = isVideoJob
            && TranscodeSpecResolver.IsMp4Container(rules.TargetContainer)
            && AudioContainerCompatibility.ContainsMp4Incompatible(media.AudioCodecs);

        // Each file's output lives under a per-media-file work root so two sources that share a
        // stem but differ by extension can never resolve to the same work path and clobber each
        // other's verified output before it is moved or replaced. A preview writes under its own
        // throwaway tree keyed by job id, kept apart from replace-bound output.
        // Black-bar removal is decided once per title and remembered on the job, so a retry
        // encodes the same picture the verified attempt did. Detection samples the same
        // deterministic windows the adaptive VMAF search uses; every failure mode yields no crop,
        // because encoding the full frame is the only answer that cannot lose picture.
        CropRect? detectedCrop = null;
        if (isVideoJob && rules.CropBlackBars && rules.TargetVideoCodec is not null)
        {
            if (job.DetectedCrop is not null)
            {
                detectedCrop = CropRect.TryParse(job.DetectedCrop);
            }
            else if (media.Width is > 0 && media.Height is > 0 && mediaDurationSeconds is > 0)
            {
                var detector = scope.ServiceProvider.GetRequiredService<CropDetectService>();
                detectedCrop = await detector.DetectAsync(
                    media.Path,
                    new PictureSize(media.Width.Value, media.Height.Value),
                    VmafWindowPlanner.PlanAdaptive(mediaDurationSeconds.Value),
                    cancellationToken);
                var decided = detectedCrop?.ToString() ?? "none";
                await WithJobAsync(job.Id, tracked => tracked.DetectedCrop = decided, cancellationToken);
            }
        }

        var spec = TranscodeSpecResolver.Resolve(
            rules,
            media.Path,
            media.RelativePath,
            isPreview
                ? WorkOutputRoot.ForPreview(_workRoot, job.Id)
                : isCalibration && job.CalibrationSessionId is { } sessionId
                    ? WorkOutputRoot.ForCalibration(_workRoot, sessionId, job.Id)
                    : WorkOutputRoot.ForMediaFile(_workRoot, media.Id),
            media.IsHdr,
            isCalibration
                ? job.RequestedVideoQuality
                : library?.QualityCrf ?? rules.DefaultCrf,
            library?.EncoderPreset,
            media.MediaKind,
            sourceHasImageSubtitles,
            sourceHasMp4IncompatibleAudio,
            media.VideoCodec,
            media.MaxAudioChannels,
            media.IsVariableFrameRate == true,
            sourceAudioLanguages,
            sourceSubtitleLanguages,
            sourceWidth: media.Width,
            sourceHeight: media.Height,
            detectedCrop: detectedCrop,
            sourceFrameRate: freshSourceProbe?.Success == true ? freshSourceProbe.VideoFrameRate : null);

        // The inventory made the job eligible, but the mandatory fresh probe is authoritative.
        // If its current track set has nothing to remove, cancel cleanly instead of producing a
        // byte-for-byte no-op that can only fail the size-reduction gate and retry forever.
        if (!isDisposable && TrackCleanupHasNoRemovalWork(
                rules.Profile,
                spec.RemoveAudioStreamIndexes?.Count ?? 0,
                spec.RemoveSubtitleStreamIndexes?.Count ?? 0))
        {
            throw new JobNoLongerEligibleException(
                "No removable tracks remain after the fresh source probe");
        }

        // A video preview only needs a short sample: encoding the whole file would be as slow as a
        // real transcode. Take it from the middle, where the content is representative rather than an
        // intro/black frames. Audio/image previews are already fast, so they run in full.
        if (isDisposable
            && (spec.Kind == MediaKind.Video && spec.VideoCodec is not null
                || isCalibration && spec.Kind == MediaKind.Audio))
        {
            var seconds = isCalibration
                ? job.CalibrationClipSeconds
                    ?? (spec.Kind == MediaKind.Audio
                        ? Optimisarr.Core.Calibration.BlindCalibrationPolicy.AudioSampleSeconds
                        : Optimisarr.Core.Calibration.BlindCalibrationPolicy.SampleSeconds)
                : PreviewClipPolicy.DurationSeconds;
            var start = isCalibration
                ? job.CalibrationClipStartSeconds ?? 0
                : PreviewClipPolicy.Plan(mediaDurationSeconds).StartSeconds;
            spec = spec with
            {
                ClipSeconds = seconds,
                ClipStartSeconds = start > 0 ? start : null,
                // A video slider preset can also change its audio/container contract (notably
                // Scott's bundle), so calibration must encode the complete preset output.
                VideoOnly = false
            };
        }

        var original = new OriginalSnapshot(
            media.Path,
            media.SizeBytes,
            mediaDurationSeconds,
            media.AudioTrackCount ?? 0,
            media.SubtitleTrackCount ?? 0,
            media.IsHdr,
            spec.TonemapToSdr,
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
            VideoReencoded: spec.VideoCodec is not null,
            ExpectedVideoCodec: spec.VideoCodec,
            // The size a downscale was told to produce. The gate holds the output to this rather
            // than to the source, and it is the same PictureSize the scale filter was built from.
            ExpectedWidth: spec.ExpectedSize?.Width,
            ExpectedHeight: spec.ExpectedSize?.Height,
            Crop: spec.CropTo,
            FrameRate: spec.FrameRate,
            // The tracks the kept-languages rules remove on purpose; verification holds
            // the output to exactly this plan and judges fidelity against the kept tracks.
            RemovedAudioStreamIndexes: spec.RemoveAudioStreamIndexes,
            RemovedSubtitleStreamIndexes: spec.RemoveSubtitleStreamIndexes,
            // Track cleanup (no codec, no target container) promises the container type
            // is untouched; verification holds the output to that promise.
            ContainerMustMatch: rules.TargetVideoCodec is null && rules.TargetContainer is null);

        // Only a video re-encode needs a hardware/software encoder resolved. A non-null
        // VideoCodec is exactly the case the command builder re-encodes video for (audio,
        // image, and remux specs all leave it null) — gate on that rather than the file's
        // MediaKind, so a video classified Unknown still gets the selected GPU encoder
        // instead of silently falling back to the CPU library encoder.
        string? videoEncoderName = null;
        EncoderQuality? videoQuality = null;
        if (spec.VideoCodec is not null)
        {
            var sourceBitDepth = PixelFormatInfo.Parse(
                freshSourceProbe?.PixelFormat ?? media.PixelFormat,
                freshSourceProbe?.BitsPerRawSample ?? media.BitsPerRawSample)?.BitDepth;
            // A worker's encoder is chosen from what it proved, in the same preference order this
            // machine uses for its own hardware. The queue's encoder mode describes this machine's
            // GPU and says nothing about the worker's, so Auto is the only honest mode there.
            var videoEncoder = placement.RemoteWorker is { } remoteWorker
                ? EncoderSelector.Select(
                    spec.VideoCodec,
                    EncoderMode.Auto,
                    WorkerEncoderCatalogue.Describe(remoteWorker.VideoEncoders),
                    sourceBitDepth)
                : await ResolveVideoEncoderAsync(
                    spec.VideoCodec,
                    queueSettings.EncoderMode,
                    sourceBitDepth,
                    cancellationToken);
            if (videoEncoder is { Succeeded: false })
            {
                throw new InvalidOperationException(videoEncoder.Error);
            }

            videoEncoderName = videoEncoder.EncoderName;
            var videoPreset = EncoderPresetPolicy.Resolve(videoEncoderName, spec.Preset);
            if (!videoPreset.Succeeded)
            {
                throw new InvalidOperationException(videoPreset.Error);
            }
            spec = spec with { Preset = videoPreset.FfmpegPreset };

            if (spec.Crf is { } requestedQuality)
            {
                videoQuality = library?.VideoQualityStrategy == VideoQualityStrategy.AdaptiveVmaf
                    && job.AdaptiveVideoQuality is { } adaptiveQuality
                    ? EncoderQualityPolicy.ResolveAdaptive(
                        videoEncoderName,
                        requestedQuality,
                        adaptiveQuality,
                        job.QualityRetryCount)
                    : EncoderQualityPolicy.Resolve(
                        videoEncoderName,
                        requestedQuality,
                        job.QualityRetryCount);
                spec = spec with { Crf = videoQuality.Effective };
            }
            logger.LogInformation(
                "Job {JobId} will encode video with '{Encoder}' (mode {Mode}, quality {QualityMode} {Effective}; requested {Requested}, retry {Retry})",
                jobId,
                videoEncoderName,
                queueSettings.EncoderMode,
                videoQuality?.Mode,
                videoQuality?.Effective,
                videoQuality?.Requested,
                videoQuality?.RetryCount);
            if (videoPreset.Effort is not null)
            {
                if (videoPreset.FfmpegPreset is null)
                {
                    logger.LogInformation(
                        "Job {JobId} requested {Effort} encoder effort; '{Encoder}' uses its driver default",
                        jobId,
                        videoPreset.Effort,
                        videoEncoderName);
                }
                else
                {
                    logger.LogInformation(
                        "Job {JobId} resolved {Effort} encoder effort to preset '{Preset}' for '{Encoder}'",
                        jobId,
                        videoPreset.Effort,
                        videoPreset.FfmpegPreset,
                        videoEncoderName);
                }
            }
        }

        // The primary command honours the hardware-decode setting except for short disposable
        // video comparisons: exact source-frame selection matters more there, and hardware decoder
        // reordering around an input seek can move the clip several frames before its VMAF
        // reference. Hardware encoding remains enabled. Normal jobs retain the existing
        // hardware-decode path and transparent software fallback.
        // A remote worker decodes in hardware only with a decoder it proved by a real decode, and
        // only for the encoder family that decoder belongs to; otherwise its command decodes in
        // software, the path every filter here is proven on. A job whose hardware-decoded candidate
        // already came back corrupt decodes in software wherever it runs next.
        var remoteHardwareDecoder = placement.RemoteWorker is { } decodingWorker
            ? RemoteHardwareDecoder(decodingWorker, videoEncoderName)
            : null;
        var hardwareDecode = !job.PreferSoftwareDecode
            && (placement.IsRemote ? remoteHardwareDecoder is not null : true)
            && HardwareDecodePolicy.ShouldUse(
            placement.IsRemote || queueSettings.HardwareDecode,
            isDisposable,
            spec.Kind,
            spec.VideoCodec,
            spec.ClipSeconds,
            // The downscale and crop are software filters; they cannot read frames a hardware
            // decoder leaves on the GPU. Software decode keeps them working; the hardware encoder is
            // unaffected. The fps filter only re-times frames and may well pass GPU surfaces through,
            // but that path is unproven here, and software decode is the one every filter is proven on.
            requiresSoftwareFilter: spec.DownscaleTo is not null
                || spec.CropTo is not null
                || spec.TargetFrameRate is not null);
        string? hardwareToneMapTransfer = null;
        var hardwareToneMapDolbyVision = media.IsDolbyVision;
        var verificationPolicy = ResolveVerificationPolicy(
            queueSettings.VerificationPolicy,
            library);
        var vmafQualityGateEnabled = verificationPolicy.QualityGateEnabled;
        var needsHardwareToneMapConfirmation = queueSettings.HdrToneMapMode == HdrToneMapMode.Hardware
            && hardwareDecode
            && spec.TonemapToSdr
            && !vmafQualityGateEnabled
            && !media.IsDolbyVision
            && (videoEncoderName?.EndsWith("_qsv", StringComparison.OrdinalIgnoreCase) == true
                || videoEncoderName?.EndsWith("_vaapi", StringComparison.OrdinalIgnoreCase) == true);
        if (needsHardwareToneMapConfirmation)
        {
            freshSourceProbe ??= await scope.ServiceProvider
                .GetRequiredService<MediaProbeService>()
                .ProbeAsync(media.Path, cancellationToken);
            if (freshSourceProbe.Success)
            {
                hardwareToneMapTransfer = freshSourceProbe.ColorTransfer;
                hardwareToneMapDolbyVision = freshSourceProbe.IsDolbyVision;
            }
            else
            {
                logger.LogWarning(
                    "Job {JobId}: source colour metadata could not be freshly confirmed for hardware tone mapping; using software. {Error}",
                    jobId,
                    freshSourceProbe.Error);
            }
        }
        var hardwareToneMap = HardwareToneMapPolicy.ShouldUse(
            queueSettings.HdrToneMapMode,
            hardwareDecode,
            spec.TonemapToSdr,
            vmafQualityGateEnabled,
            hardwareToneMapDolbyVision,
            hardwareToneMapTransfer,
            videoEncoderName);
        // A remote command names no path on this machine: the worker substitutes its own copy of
        // the source and its own scratch output, and the output keeps the container extension the
        // contract chose. The thread limit is this machine's, so a worker receives none.
        if (placement.IsRemote)
        {
            spec = spec with
            {
                InputPath = WorkerProtocol.InputPlaceholder,
                OutputPath = WorkerProtocol.OutputPlaceholder + Path.GetExtension(spec.OutputPath)
            };
        }
        var threads = placement.IsRemote ? 0 : queueSettings.CpuThreadLimit;
        var primaryArguments = FfmpegCommandBuilder.Build(
            spec,
            threads,
            videoEncoderName,
            OptimisedMarkerValue,
            hardwareDecode,
            hardwareToneMap);
        var softwareArguments = FfmpegCommandBuilder.Build(
            spec,
            threads,
            videoEncoderName,
            OptimisedMarkerValue,
            hardwareDecode: false,
            hardwareToneMap: false);
        var usedHardwareDecode = !primaryArguments.SequenceEqual(softwareArguments);

        return new JobWork(
            spec,
            primaryArguments,
            videoEncoderName,
            videoQuality,
            isDisposable,
            isCalibration,
            mediaDurationSeconds,
            freshSourceProbe?.Success == true ? freshSourceProbe.FrameCount : media.FrameCount,
            freshSourceProbe?.Success == true ? freshSourceProbe.VideoFrameRate : null,
            library?.MoveOnComplete ?? false,
            library?.TargetFolder,
            library?.MoveOverwrite ?? false,
            media.Id,
            original,
            verificationPolicy,
            library?.VideoQualityStrategy ?? VideoQualityStrategy.Fixed,
            library?.WorkPlacement ?? WorkPlacement.Anywhere,
            job.AdaptiveVideoQuality,
            library?.AutoReplace ?? false,
            queueSettings.CpuThreadLimit,
            hardwareDecode,
            hardwareToneMap,
            usedHardwareDecode ? softwareArguments : null,
            usedHardwareDecode,
            hardwareToneMap,
            media.Width is > 0 && media.Height is > 0
                ? new PictureSize(media.Width.Value, media.Height.Value)
                : null,
            SoftwareDecodeRetryReason: job.PreferSoftwareDecode ? HardwareDecodeFallback.SoftwareDecodeRetryReason : null,
            BypassSizePreflight: job.BypassSizePreflight);
    }

    /// <summary>
    /// The decoder a worker's command may use: VideoToolbox, when the worker proved it and the
    /// encoder is VideoToolbox too. Other families are not paired with a worker decoder yet.
    /// </summary>
    /// <summary>
    /// How far into its container a file's first picture sits, or null when ffprobe reported no
    /// usable starts. Shared with local verification so both measure the same quantity.
    /// </summary>
    internal static double? ContainerLeadSeconds(MediaProbeResult probe) =>
        probe.Success && probe.VideoStartSeconds is { } video && probe.ContainerStartSeconds is { } container
            ? video - container
            : null;

    private static string? RemoteHardwareDecoder(WorkerCapabilities worker, string? videoEncoder) =>
        videoEncoder is not null
        && videoEncoder.EndsWith("_videotoolbox", StringComparison.OrdinalIgnoreCase)
        && worker.HardwareDecoders.Any(decoder => string.Equals(decoder, "videotoolbox", StringComparison.OrdinalIgnoreCase))
            ? "videotoolbox"
            : null;

    /// <summary>
    /// Runs a bounded, fail-open preparation search for an adaptive library. Every candidate uses
    /// the real picture contract and the canonical VMAF scorer over deterministic source windows.
    /// Preparation may choose an encoder quality; it can never authorise replacement, and any
    /// unavailable/noisy evidence falls back to the library's value before the normal full encode.
    /// </summary>
    private async Task<JobWork?> SelectAdaptiveQualityAsync(
        int jobId,
        JobWork work,
        CancellationToken cancellationToken)
    {
        var baseline = work.VideoQuality!.Effective;
        var scratchRoot = Path.Combine(
            Path.GetDirectoryName(work.Spec.OutputPath)!,
            $".adaptive-quality-{jobId}-{Guid.NewGuid():N}");

        await WithJobAsync(jobId, job =>
        {
            job.Status = JobStatus.Probing;
            job.Progress = AdaptiveQualityProgress.Started;
            job.UpdatedAt = DateTimeOffset.UtcNow;
        }, cancellationToken);
        await NotifyAsync();

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var probeService = scope.ServiceProvider.GetRequiredService<MediaProbeService>();
            var qualityService = scope.ServiceProvider.GetRequiredService<QualityScoreService>();
            var sourceProbe = await probeService.ProbeAsync(work.Original.Path, cancellationToken);
            if (!sourceProbe.Success || sourceProbe.Width is not > 0 || sourceProbe.Height is not > 0)
            {
                return await FinishAdaptiveSelectionAsync(
                    jobId,
                    work,
                    baseline,
                    fellBack: true,
                    sourceProbe.Error ?? "source dimensions are unavailable",
                    cancellationToken);
            }

            // A subtitle, chapter, attachment, or data stream may extend the container far beyond
            // the actual programme. Place every adaptive sample on the primary picture timeline so
            // a middle/late seek cannot land after EOF and create an empty, unscorable candidate.
            var samplingDuration = MediaTimelineDuration.Resolve(
                MediaKind.Video,
                sourceProbe.VideoDurationSeconds,
                work.DurationSeconds);
            if (samplingDuration is not > 0)
            {
                return await FinishAdaptiveSelectionAsync(
                    jobId, work, baseline, fellBack: true, "source video duration is unavailable", cancellationToken);
            }

            var policy = work.VerificationPolicy;
            if (!policy.QualityGateEnabled)
            {
                return await FinishAdaptiveSelectionAsync(
                    jobId, work, baseline, fellBack: true, "the VMAF target is disabled", cancellationToken,
                    expected: true);
            }

            var windows = VmafWindowPlanner.PlanAdaptive(samplingDuration.Value);
            if (windows.Count == 0)
            {
                return await FinishAdaptiveSelectionAsync(
                    jobId, work, baseline, fellBack: true, "no representative windows could be planned", cancellationToken);
            }

            var maximumCandidateBytes = SizeBudget.MaxCandidateBytes(
                work.Original.SizeBytes,
                policy.RequireSizeReduction,
                work.IsDisposable,
                policy.MinimumSizeSavingPercent);
            var sizeBasis = maximumCandidateBytes is null || work.BypassSizePreflight
                ? null
                : await MeasureSizeForecastBasisAsync(
                    jobId, work, windows, sourceProbe, samplingDuration.Value, cancellationToken);

            Directory.CreateDirectory(scratchRoot);
            var measured = new List<AdaptiveQualityProbe>();
            while (true)
            {
                var decision = AdaptiveQualitySearch.Decide(baseline, measured);
                var sizeReview = SizePreflight.Review(
                    sizeBasis,
                    measured,
                    measured.LastOrDefault(),
                    decision,
                    maximumCandidateBytes,
                    work.BypassSizePreflight);
                if (sizeReview.ShouldHold)
                {
                    await HoldForSizeReviewAsync(jobId, decision.SelectedQuality, sizeReview, cancellationToken);
                    return null;
                }

                if (decision.Complete)
                {
                    LogSizeForecast(logger, jobId, sizeReview);
                    return await FinishAdaptiveSelectionAsync(
                        jobId,
                        RebuildWorkAtQuality(work, decision.SelectedQuality),
                        decision.SelectedQuality,
                        decision.FellBack,
                        decision.Reason,
                        cancellationToken);
                }

                var candidate = await MeasureAdaptiveCandidateAsync(
                    jobId,
                    work,
                    decision.NextQuality!.Value,
                    measured.Count,
                    windows,
                    samplingDuration.Value,
                    sourceProbe,
                    policy,
                    qualityService,
                    scratchRoot,
                    cancellationToken);
                if (candidate is null)
                {
                    return await FinishAdaptiveSelectionAsync(
                        jobId,
                        work,
                        baseline,
                        fellBack: true,
                        "a sample encode or VMAF measurement was unavailable",
                        cancellationToken);
                }

                measured.Add(candidate);
                logger.LogInformation(
                    "Job {JobId}: adaptive quality candidate {Quality} {Outcome} the VMAF target with {EncodedBytes} encoded video bytes ({Scores})",
                    jobId,
                    candidate.Quality,
                    candidate.MeetsTarget ? "met" : "missed",
                    candidate.EncodedBytes,
                    AdaptiveProbeReport.Describe(candidate, policy));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Job {JobId}: adaptive quality preparation failed; using the library quality {Quality}",
                jobId,
                baseline);
            return await FinishAdaptiveSelectionAsync(
                jobId, work, baseline, fellBack: true, ex.Message, CancellationToken.None);
        }
        finally
        {
            DeleteDirectoryQuietly(scratchRoot);
        }
    }

    private async Task<AdaptiveQualityProbe?> MeasureAdaptiveCandidateAsync(
        int jobId,
        JobWork work,
        int qualityValue,
        int candidateIndex,
        IReadOnlyList<VmafWindow> windows,
        double sourceVideoDurationSeconds,
        MediaProbeResult sourceProbe,
        VerificationPolicy policy,
        QualityScoreService qualityService,
        string scratchRoot,
        CancellationToken cancellationToken)
    {
        var results = new List<QualityResult>(windows.Count);
        var windowBytesMeasured = new List<long>(windows.Count);
        for (var index = 0; index < windows.Count; index++)
        {
            var window = windows[index];
            var outputPath = Path.Combine(
                scratchRoot,
                $"q{qualityValue}-window{index}{Path.GetExtension(work.Spec.OutputPath)}");
            var sampleSpec = work.Spec with
            {
                OutputPath = outputPath,
                Crf = qualityValue,
                ClipStartSeconds = window.StartSeconds,
                ClipSeconds = window.DurationSeconds,
                // VMAF judges the primary picture only. Excluding unrelated tracks makes the
                // measured bytes a real video-size comparison and prevents copied audio or
                // subtitles from dominating a bounded quality probe.
                VideoOnly = true
            };
            var primary = FfmpegCommandBuilder.Build(
                sampleSpec,
                work.CpuThreadLimit,
                work.VideoEncoder,
                // Candidate windows seek into independently encoded long-GOP sources. Keep source
                // decode in software so decoder reordering cannot select adjacent frames and make
                // motion look like compression damage; the actual hardware encoder, video codec,
                // container, and picture filters remain unchanged. VMAF-gated HDR tone mapping
                // already uses the canonical software transform for the full job too.
                hardwareDecode: false,
                hardwareToneMap: false);

            var run = await RunFfmpegAsync(
                jobId,
                outputPath,
                primary,
                window.DurationSeconds,
                FrameRatePlanner.ScaleFrameCount(
                    FfmpegProgressCalculator.ExpectedFramesForWindow(
                        sourceProbe.FrameCount,
                        sourceVideoDurationSeconds,
                        window.DurationSeconds,
                        sourceProbe.VideoFrameRate),
                    sourceProbe.VideoFrameRate,
                    work.Spec.TargetFrameRate),
                IsHardwareEncoder(work.VideoEncoder),
                cancellationToken,
                progressMap: progress => AdaptiveQualityProgress.Map(
                    candidateIndex,
                    index,
                    windows.Count,
                    AdaptiveQualityPhase.Encoding,
                    progress));
            if (run.ExitCode != 0)
            {
                logger.LogWarning(
                    "Job {JobId}: adaptive sample {Sample} at quality {Quality} failed: {Error}",
                    jobId,
                    index + 1,
                    qualityValue,
                    run.Error);
                return null;
            }

            var windowBytes = new FileInfo(outputPath).Length;
            if (windowBytes <= 0)
            {
                logger.LogWarning(
                    "Job {JobId}: adaptive sample {Sample} at quality {Quality} produced no measurable bytes",
                    jobId,
                    index + 1,
                    qualityValue);
                return null;
            }
            windowBytesMeasured.Add(windowBytes);

            var context = new QualityMeasurementContext(
                sourceProbe.Width!.Value,
                sourceProbe.Height!.Value,
                work.Original.IsHdr,
                work.Original.HdrConvertedToSdr,
                ReferenceStartSeconds: window.StartSeconds,
                ReferenceDurationSeconds: sourceVideoDurationSeconds,
                DistortedStartSeconds: null,
                MeasureDurationSeconds: window.DurationSeconds,
                FrameSubsample: policy.VmafFrameSubsample,
                // Selection is a correctness decision rather than a throughput optimisation.
                // Software decode gives repeatable frame ordering across CPU/QSV/NVENC/VA-API.
                Acceleration: VmafAcceleration.None,
                // A capped encode kept every other frame; decimating the reference the same way
                // lines the judged frames up with the kept ones.
                ReferenceFrameRate: work.Spec.TargetFrameRate ?? sourceProbe.VideoFrameRate,
                ReferenceCrop: work.Spec.CropTo,
                ReferenceDecimation: work.Spec.FrameRate,
                // As on a worker: the candidate is a clip cut out of the source, so the reference
                // window is cut before its cadence is normalised rather than after.
                DistortedIsCutClip: true);
            var measurementProgress = new Progress<double>(progress =>
            {
                var mapped = AdaptiveQualityProgress.Map(
                    candidateIndex,
                    index,
                    windows.Count,
                    AdaptiveQualityPhase.Measuring,
                    progress);
                _ = UpdateProgressAsync(jobId, mapped);
                _ = BroadcastProgressAsync(jobId, mapped, null, null, null);
            });
            var result = await qualityService.MeasureAsync(
                work.Original.Path,
                outputPath,
                context,
                cancellationToken,
                measurementProgress);
            result = await VmafSoftwareConfirmation.ConfirmAsync(
                result,
                policy,
                () => qualityService.MeasureAsync(
                    work.Original.Path,
                    outputPath,
                    context with { Acceleration = VmafAcceleration.None },
                    cancellationToken));
            if (!result.Measured || result.Scores is null)
            {
                logger.LogWarning(
                    "Job {JobId}: adaptive sample {Sample} at quality {Quality} could not be scored: {Error}",
                    jobId,
                    index + 1,
                    qualityValue,
                    result.Error);
                return null;
            }

            results.Add(result);
            AdaptiveQualityScratch.DeleteSample(outputPath);
        }

        var combined = QualityScoreAggregator.Combine(
            results,
            "Adaptive early, middle and late samples");
        return combined.Scores is { } scores
            ? new AdaptiveQualityProbe(
                qualityValue,
                VmafSoftwareConfirmation.MeetsGate(scores, policy),
                windowBytesMeasured.Sum(),
                scores,
                windowBytesMeasured)
            : null;
    }

    private async Task<JobWork> FinishAdaptiveSelectionAsync(
        int jobId,
        JobWork work,
        int selectedQuality,
        bool fellBack,
        string reason,
        CancellationToken cancellationToken,
        // True only where falling back is the configured answer rather than a failure to learn
        // anything: a library with no quality gate has nothing for a search to measure against.
        bool expected = false)
    {
        await WithJobAsync(jobId, job =>
        {
            job.Status = JobStatus.Transcoding;
            job.Progress = 0;
            job.AdaptiveVideoQuality = selectedQuality;
            job.UpdatedAt = DateTimeOffset.UtcNow;
        }, cancellationToken);
        // A fall-back is a warning, not news. The search running and learning nothing is the
        // feature not working, and logged at the same level as a success it is indistinguishable
        // from one: a hundred and seven jobs fell back over a fortnight, encoded at the library's
        // quality, came out larger than their sources and failed the size gate, and the line saying
        // so scrolled past among the ordinary ones.
        //
        // A library with no quality gate is the exception. There is nothing to search against and
        // nothing wrong, so that stays ordinary information.
        logger.Log(
            AdaptiveSelectionOutcome.SeverityOf(fellBack, expected),
            "Job {JobId}: adaptive quality {Outcome}; full encode will use {Mode} {Quality}. {Reason}",
            jobId,
            AdaptiveSelectionOutcome.Describe(fellBack, expected),
            work.VideoQuality?.Mode,
            selectedQuality,
            reason);
        await NotifyAsync();
        return work;
    }

    private async Task HoldForSizeReviewAsync(
        int jobId,
        int selectedQuality,
        SizePreflightAssessment forecast,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await WithJobAsync(jobId, job =>
        {
            if (job.Status != JobStatus.Probing)
            {
                return;
            }
            job.Status = JobStatus.AwaitingSizeReview;
            job.AdaptiveVideoQuality = selectedQuality;
            job.Progress = 0;
            job.ErrorMessage = forecast.Reason;
            job.UpdatedAt = DateTimeOffset.UtcNow;
        }, cancellationToken);
        logger.LogInformation(
            "Job {JobId}: full encode held for size review; samples were {Ratio:P1} of the source video over the same scenes, projecting {ProjectedBytes} bytes",
            jobId, forecast.VideoRatio, forecast.ProjectedBytes);
        await NotifyAsync();
    }

    /// <summary>
    /// What the source itself spent on the sample windows, for the size forecast. Null, with the
    /// reason logged, when the windows cannot be read: the forecast then abstains and the final
    /// size gate is left to decide, exactly as before any forecast existed.
    /// </summary>
    private async Task<SizeForecastBasis?> MeasureSizeForecastBasisAsync(
        int jobId,
        JobWork work,
        IReadOnlyList<VmafWindow> windows,
        MediaProbeResult sourceProbe,
        double samplingDurationSeconds,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var measured = await scope.ServiceProvider
            .GetRequiredService<ISourceWindowBytesProbe>()
            .MeasureAsync(work.Original.Path, windows, sourceProbe.ContainerStartSeconds, cancellationToken);
        var basis = measured is null
            ? null
            : SizeForecastBasis.From(work.Original.SizeBytes, measured, work.Spec, samplingDurationSeconds);
        if (basis is null)
        {
            logger.LogInformation(
                "Job {JobId}: source bytes over the sample windows could not be read; no size forecast for this search",
                jobId);
        }
        return basis;
    }

    /// <summary>
    /// The size-forecast basis for a search a worker is running. Measured here, on the control
    /// plane's own copy of the source, because the worker's copy is the same bytes and the
    /// judgement belongs here; the worker only reports what its samples came to.
    /// </summary>
    public async Task<SizeForecastBasis?> MeasureSizeForecastBasisAsync(
        int jobId,
        WorkerCapabilities worker,
        CancellationToken cancellationToken)
    {
        JobWork? work;
        try
        {
            work = await LoadWorkAsync(jobId, new EncodePlacement(worker), cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        if (work is not { } loaded)
        {
            return null;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var sourceProbe = await scope.ServiceProvider
            .GetRequiredService<IMediaProbeService>()
            .ProbeAsync(loaded.Original.Path, cancellationToken);
        // The same windows the worker was asked to encode, planned from the same timeline.
        var samplingDuration = MediaTimelineDuration.Resolve(
            MediaKind.Video,
            sourceProbe.VideoDurationSeconds,
            loaded.DurationSeconds);
        if (samplingDuration is not > 0)
        {
            return null;
        }

        return await MeasureSizeForecastBasisAsync(
            jobId,
            loaded,
            VmafWindowPlanner.PlanAdaptive(samplingDuration.Value),
            sourceProbe,
            samplingDuration.Value,
            cancellationToken);
    }

    /// <summary>
    /// Every forecast that let a job through is logged beside the one that held a job, so its
    /// accuracy can be checked against the finished file rather than taken on trust.
    /// </summary>
    internal static void LogSizeForecast(ILogger logger, int jobId, SizePreflightAssessment forecast)
    {
        if (forecast.ProjectedBytes is { } projected)
        {
            logger.LogInformation(
                "Job {JobId}: size forecast — samples were {Ratio:P1} of the source video over the same scenes, projecting {ProjectedBytes} bytes ({Change:+0.0;-0.0}% against the source)",
                jobId, forecast.VideoRatio, projected, forecast.ProjectedPercentChange);
        }
    }

    private static JobWork RebuildWorkAtQuality(JobWork work, int selectedQuality)
    {
        var spec = work.Spec with { Crf = selectedQuality };
        var primary = FfmpegCommandBuilder.Build(
            spec,
            work.CpuThreadLimit,
            work.VideoEncoder,
            OptimisedMarkerValue,
            work.HardwareDecodeRequested,
            work.HardwareToneMapRequested);
        var software = FfmpegCommandBuilder.Build(
            spec,
            work.CpuThreadLimit,
            work.VideoEncoder,
            OptimisedMarkerValue,
            hardwareDecode: false,
            hardwareToneMap: false);
        return work with
        {
            Spec = spec,
            Arguments = primary,
            VideoQuality = work.VideoQuality! with { Effective = selectedQuality },
            SoftwareFallbackArguments = primary.SequenceEqual(software) ? null : software,
            UsedHardwareDecode = !primary.SequenceEqual(software)
        };
    }

    private static void DeleteDirectoryQuietly(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Scratch cleanup is also covered by the timed/orphan cleanup path.
        }
    }

    internal static bool TrackCleanupHasNoRemovalWork(
        RuleProfile profile,
        int audioRemovalCount,
        int subtitleRemovalCount) =>
        profile == RuleProfile.TrackCleanup && audioRemovalCount + subtitleRemovalCount == 0;

    private sealed class JobNoLongerEligibleException(string message) : Exception(message);

    private sealed record FfmpegRun(int ExitCode, string? Error, string? Log, bool SizeGateFailed = false);

    private async Task<FfmpegRun> RunFfmpegAsync(
        int jobId,
        string outputPath,
        IReadOnlyList<string> arguments,
        double? durationSeconds,
        int? expectedFrameCount,
        bool hardwareEncoder,
        CancellationToken cancellationToken,
        bool reportProgress = true,
        Func<double, double>? progressMap = null,
        long? maxCandidateBytes = null,
        long? minCandidateBytes = null)
    {
        // Every attempt must recreate the directory: rejecting a candidate prunes its empty
        // parent. Reserve it before creation so another job's cleanup cannot remove it while
        // FFmpeg is opening the output; keep that reservation through retries and cancellation.
        using var outputDirectory = WorkPaths.PrepareOutputDirectory(outputPath, ReserveWorkDirectory);
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
        // A job claimed just before a manual pause landed must not keep encoding through it.
        pauseManager.OnEncodeStarted(process.Id);

        // FFmpeg's machine-readable progress protocol is isolated on stdout. Stderr remains a
        // diagnostic stream, and both pipes are consumed concurrently so neither can block FFmpeg.
        var stallMonitor = new EncodeStallMonitor(DateTimeOffset.UtcNow);
        var progressTask = ReadProgressAsync(
            process,
            jobId,
            durationSeconds,
            expectedFrameCount,
            reportProgress,
            progressMap,
            stallMonitor,
            cancellationToken);
        var stderrTask = ReadStderrAsync(process, cancellationToken);

        EncodeWaitResult? stopped = null;
        try
        {
            stopped = await WaitForExitOrStallAsync(
                process, jobId, stallMonitor, outputPath, maxCandidateBytes, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            KillQuietly(process);
            try
            {
                // Observe both readers after terminating their writer. Cancellation is already the
                // outcome, so any shutdown-only pipe exception must not replace it.
                await Task.WhenAll(progressTask, stderrTask);
            }
            catch (Exception)
            {
                // The original cancellation remains authoritative.
            }
            throw;
        }

        await progressTask;
        var stderr = await stderrTask;
        if (stopped?.BudgetExceededAtBytes is { } observed)
        {
            return new FfmpegRun(-1,
                $"Size saving: candidate reached {observed:n0} bytes, exceeding the {maxCandidateBytes:n0}-byte budget before encoding finished.",
                stderr.Log,
                SizeGateFailed: true);
        }
        if (stopped?.Stall is { } stalledAs)
        {
            // A killed process reports a signal exit, never zero; the guard keeps a stall from ever
            // being read as success should a platform report otherwise.
            var exitCode = process.ExitCode == 0 ? -1 : process.ExitCode;
            return new FfmpegRun(exitCode, stallMonitor.Describe(stalledAs), stderr.Log);
        }

        // FFmpeg can finish and write its final mux overhead between budget polls. Apply the
        // frozen limit once more before any decode or VMAF work on the completed file.
        if (process.ExitCode == 0 && maxCandidateBytes is { } maximum
            && TryReadOutputSize(outputPath) is { } finalBytes
            && SizeBudget.Exceeded(finalBytes, maximum))
        {
            return new FfmpegRun(-1,
                $"Size saving: finished candidate is {finalBytes:n0} bytes, exceeding the {maximum:n0}-byte budget.",
                stderr.Log,
                SizeGateFailed: true);
        }

        if (process.ExitCode == 0 && minCandidateBytes is { } minimum
            && TryReadOutputSize(outputPath) is { } finalSize
            && SizeBudget.Below(finalSize, minimum))
        {
            return new FfmpegRun(-1,
                $"Compression ceiling: finished candidate is {finalSize:n0} bytes, below the {minimum:n0}-byte floor.",
                stderr.Log,
                SizeGateFailed: true);
        }

        return process.ExitCode == 0
            ? new FfmpegRun(process.ExitCode, null, null)
            : new FfmpegRun(process.ExitCode, stderr.Tail, stderr.Log);
    }

    private static readonly TimeSpan StallCheckInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SizeBudgetCheckInterval = TimeSpan.FromSeconds(2);

    // Waits for ffmpeg to exit, asking the stall monitor on a timer whether it still deserves the
    // wait. A stalled process is killed and the kind of stall returned so the caller can fail the
    // job with a reason instead of holding a queue slot until someone restarts the container.
    private sealed record EncodeWaitResult(EncodeStallKind? Stall = null, long? BudgetExceededAtBytes = null);

    private async Task<EncodeWaitResult?> WaitForExitOrStallAsync(
        Process process,
        int jobId,
        EncodeStallMonitor stallMonitor,
        string outputPath,
        long? maxCandidateBytes,
        CancellationToken cancellationToken)
    {
        var exited = process.WaitForExitAsync(cancellationToken);
        var checkInterval = maxCandidateBytes is null ? StallCheckInterval : SizeBudgetCheckInterval;
        while (await Task.WhenAny(exited, Task.Delay(checkInterval, cancellationToken)) != exited)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (maxCandidateBytes is { } maximum
                && TryReadOutputSize(outputPath) is { } bytes
                && SizeBudget.Exceeded(bytes, maximum))
            {
                logger.LogInformation(
                    "Job {JobId}: size-saving budget exceeded at {ObservedBytes} bytes (limit {MaxBytes}); stopping encode",
                    jobId, bytes, maximum);
                KillQuietly(process);
                await exited;
                return new EncodeWaitResult(BudgetExceededAtBytes: bytes);
            }
            if (stallMonitor.Check(DateTimeOffset.UtcNow, pauseManager.IsPaused) is not { } stall)
            {
                continue;
            }

            logger.LogWarning("Job {JobId}: {Reason}", jobId, stallMonitor.Describe(stall));
            KillQuietly(process);
            await exited;
            return new EncodeWaitResult(Stall: stall);
        }

        await exited;
        return null;
    }

    private static long? TryReadOutputSize(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private sealed record FfmpegStderr(string? Tail, string? Log);

    // Reads the stable -progress protocol from stdout. Persisted progress is throttled to roughly
    // one-percent steps, while live telemetry is sent for every block. Both paths fail independently
    // and recover on the next block so a transient database or SignalR error cannot stop pipe
    // consumption and deadlock an otherwise healthy encode.
    private async Task ReadProgressAsync(
        Process process,
        int jobId,
        double? durationSeconds,
        int? expectedFrameCount,
        bool reportProgress,
        Func<double, double>? progressMap,
        EncodeStallMonitor stallMonitor,
        CancellationToken cancellationToken)
    {
        var parser = new FfmpegProgressProtocolParser();
        var lastObserved = 0.0;
        var lastPersisted = 0.0;
        var hasPersisted = false;
        var persistenceWarningLogged = false;
        var broadcastWarningLogged = false;

        string? line;
        while ((line = await process.StandardOutput.ReadLineAsync(cancellationToken)) is not null)
        {
            // Every line is proof of life, parsed or not; the final block starts the exit clock.
            var parsed = parser.ParseLine(line);
            if (parsed is { IsFinal: true })
            {
                stallMonitor.FinalReported(DateTimeOffset.UtcNow);
            }
            else
            {
                stallMonitor.Touch(DateTimeOffset.UtcNow);
            }

            if (parsed is not { } sample || !reportProgress)
            {
                continue;
            }

            // The final block is worth reporting even when it carries nothing measurable (a source
            // with neither a duration nor a frame count): "finishing" is a fact about the process,
            // not a reading of the bar.
            var reading = FfmpegProgressCalculator.Measure(durationSeconds, expectedFrameCount, sample);
            var estimateExhausted = reading?.EstimateExhausted ?? false;
            if (reading is not { } measuredReading)
            {
                if (!sample.IsFinal)
                {
                    continue;
                }

                measuredReading = new FfmpegProgressReading(lastObserved, EstimateExhausted: false);
            }
            var measuredProgress = measuredReading.Progress;

            // Some inputs contain discontinuous timestamps. Never let one make the visible or
            // persisted bar move backwards.
            var progress = Math.Max(
                lastObserved,
                progressMap?.Invoke(measuredProgress) ?? measuredProgress);
            lastObserved = progress;

            if (progress > lastPersisted && (!hasPersisted || progress - lastPersisted >= 0.01))
            {
                try
                {
                    await UpdateProgressAsync(jobId, progress);
                    lastPersisted = progress;
                    hasPersisted = true;
                }
                catch (Exception ex)
                {
                    if (!persistenceWarningLogged)
                    {
                        logger.LogWarning(
                            ex,
                            "Job {JobId}: progress persistence failed; continuing to consume FFmpeg progress",
                            jobId);
                        persistenceWarningLogged = true;
                    }
                }
            }

            // Two cases have nothing left to estimate. After FFmpeg's final block the output is
            // complete and only the exit remains, so the UI is told "finishing". When every clock
            // has run past its expected end the encode is still going but the expectation was
            // wrong, so a seconds-left figure would be computed from a bar that can no longer
            // move; speed and fps stay, the estimate goes.
            var eta = !sample.IsFinal && !estimateExhausted && sample.Speed is { } speed && durationSeconds is > 0
                ? FfmpegProgressParser.EstimateRemainingSeconds(
                    durationSeconds.Value,
                    measuredProgress * durationSeconds.Value,
                    speed)
                : null;
            try
            {
                await BroadcastProgressAsync(jobId, progress, sample.Fps, sample.Speed, eta, sample.IsFinal);
            }
            catch (Exception ex)
            {
                if (!broadcastWarningLogged)
                {
                    logger.LogWarning(
                        ex,
                        "Job {JobId}: live progress broadcast failed; continuing to consume FFmpeg progress",
                        jobId);
                    broadcastWarningLogged = true;
                }
            }
        }
    }

    // Keeps the last few stderr lines for a one-line failure message and the complete bounded
    // diagnostic stream for the API. Progress is on stdout, so no warning or error is filtered out.
    // The tail holds distinct lines: FFmpeg repeats a warning once per stream, and a file with
    // twenty audio tracks buries the one line that says what actually went wrong under twenty
    // identical copies of something harmless. The full log keeps every copy.
    private static async Task<FfmpegStderr> ReadStderrAsync(
        Process process,
        CancellationToken cancellationToken)
    {
        var tail = new Queue<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var log = new FfmpegLogBuffer();

        string? line;
        while ((line = await process.StandardError.ReadLineAsync(cancellationToken)) is not null)
        {
            log.Append(line);
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

        return new FfmpegStderr(
            tail.Count > 0 ? string.Join('\n', tail) : null,
            log.ToLog());
    }

    // Translate known ffmpeg failures into a clear, actionable reason; fall back to the raw
    // stderr tail for anything unrecognised. The partial output is never left behind.
    private async Task FailFromFfmpegRunAsync(int jobId, string outputPath, FfmpegRun run)
    {
        DeleteWorkOutput(outputPath);
        var error = FfmpegErrorInterpreter.Explain(run.Error)
            ?? run.Error
            ?? $"ffmpeg exited with code {run.ExitCode}";
        await CompleteAsync(jobId, JobStatus.Failed, error: error, processLog: run.Log,
            immediateAutoExclusion: run.SizeGateFailed
                ? ImmediateAutoExclusionReason.SizeSaving
                : ImmediateAutoExclusionReason.None);
        await NotifyJobFailedAsync(jobId, error);
    }

    private async Task BeginTranscodeAsync(
        int jobId,
        string outputPath,
        IReadOnlyList<string> arguments,
        string? videoEncoder,
        EncoderQuality? videoQuality,
        CancellationToken cancellationToken)
    {
        await WithJobAsync(jobId, job =>
        {
            job.WorkOutputPath = outputPath;
            job.FfmpegArguments = string.Join(' ', arguments);
            job.VideoEncoder = videoEncoder;
            job.RequestedVideoQuality = videoQuality?.Requested;
            job.EffectiveVideoQuality = videoQuality?.Effective;
            job.VideoQualityMode = videoQuality?.Mode;
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
        int jobId,
        JobStatus status,
        double? progress = null,
        string? error = null,
        string? processLog = null,
        ImmediateAutoExclusionReason immediateAutoExclusion = ImmediateAutoExclusionReason.None)
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

            await ApplyFailureTrackingAsync(db, job, status, immediateAutoExclusion);
            await db.SaveChangesAsync();
        }
        finally
        {
            _dbLock.Release();
        }
    }

    // Begin a fresh attempt on a claimed job. A retry must not carry the previous attempt's
    // failure state: clearing the error and its classification means a job that later succeeds is
    // no longer grouped as a failure (the stale-category bug), and a retry in flight never shows a
    // reason from the run before it. Internal so the pure field reset can be unit tested.
    internal static void PrepareForAttempt(Job job, DateTimeOffset nowUtc)
    {
        job.Status = JobStatus.Transcoding;
        job.Attempt += 1;
        job.ExecutionAttempt += 1;
        job.StartedAt = nowUtc;
        job.UpdatedAt = nowUtc;
        job.ErrorMessage = null;
        job.FailureCategory = null;
        job.ProcessLog = null;
        job.FfmpegArguments = null;
        job.VideoEncoder = null;
        // Calibration's requested quality is the candidate being tested, not a stale result.
        if (job.Type == JobType.Normal) job.RequestedVideoQuality = null;
        job.EffectiveVideoQuality = null;
        job.VideoQualityMode = null;
        job.WorkOutputPath = null;
        job.OutputSizeBytes = null;
        job.VerificationPassed = null;
        job.VerificationReportJson = null;
        job.VerifiedAt = null;
        job.FinishedAt = null;
    }

    // Keep a durable per-file failure tally so a file that keeps failing is excluded automatically
    // (and shown on the Excluded tab) rather than offered forever. Deterministic size/VMAF outcomes
    // can exclude immediately; a successful encode clears the streak. Excluding here never touches
    // the original — it only stops the file being re-offered.
    // Internal so the pure DB effect can be unit tested without standing up the whole dispatcher.
    internal static async Task ApplyFailureTrackingAsync(
        OptimisarrDbContext db,
        Job job,
        JobStatus status,
        ImmediateAutoExclusionReason immediateAutoExclusion = ImmediateAutoExclusionReason.None)
    {
        // Interactive comparison work is disposable evidence, not an optimisation attempt. A bad
        // preview/calibration clip must never count against, exclude, or clear the source file.
        if (job.Type != JobType.Normal)
        {
            return;
        }

        var media = await db.MediaFiles.FirstOrDefaultAsync(f => f.Id == job.MediaFileId);
        if (media is null)
        {
            return;
        }

        if (status == JobStatus.Failed)
        {
            media.FailureCount += 1;
            media.UpdatedAt = DateTimeOffset.UtcNow;

            if ((immediateAutoExclusion != ImmediateAutoExclusionReason.None
                    || AutoExclusionPolicy.ShouldExclude(media.FailureCount, AutoExcludeFailureThreshold))
                && !await db.Exclusions.AnyAsync(e => e.Path == media.Path))
            {
                db.Exclusions.Add(new Exclusion
                {
                    Path = media.Path,
                    LibraryId = media.LibraryId,
                    RelativePath = media.RelativePath,
                    Reason = immediateAutoExclusion switch
                    {
                        ImmediateAutoExclusionReason.SizeSaving =>
                            "Auto-excluded after the output failed a configured size gate",
                        ImmediateAutoExclusionReason.VmafAfterHigherQualityRetry =>
                            "Auto-excluded after VMAF failed the higher-quality retry",
                        ImmediateAutoExclusionReason.VmafAtMaximumQuality =>
                            "Auto-excluded after VMAF failed at the maximum encoder quality",
                        _ => $"Auto-excluded after {media.FailureCount} failed attempts"
                    },
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
    private enum VerificationDisposition
    {
        /// <summary>The job reached a terminal state, a queued retry, or replacement.</summary>
        Finished,

        /// <summary>
        /// The hardware-decoded output failed the way a corrupt decode fails. The report is saved;
        /// the caller owns the software re-encode because only it holds the fallback command.
        /// </summary>
        RetryWithSoftwareDecode
    }

    private async Task<VerificationDisposition> VerifyAndFinishAsync(
        int jobId,
        string outputPath,
        JobWork work,
        CancellationToken cancellationToken,
        bool softwareDecodeRetryAvailable = false,
        RemoteQuality? remoteQuality = null,
        RemoteVerificationEvidence? remoteEvidence = null)
    {
        await WithJobAsync(jobId, job =>
        {
            job.Status = JobStatus.Verifying;
            // The transcode left progress at ~100%; reset it so the verification (VMAF) pass
            // reports its own 0..100% rather than appearing already finished.
            job.Progress = 0;
            job.UpdatedAt = DateTimeOffset.UtcNow;
        }, cancellationToken);
        await NotifyAsync();

        var settings = await GetQueueSettingsAsync(cancellationToken);
        var policy = work.VerificationPolicy;
        if (work.IsCalibration)
        {
            policy = policy with
            {
                RequireSizeReduction = false,
                QualityGateEnabled = false,
                // The quality lab reveals VMAF as objective evidence after the user's blind
                // ratings. It is deliberately measure-only: a score never rejects a sample or
                // overrides the user's preference.
                MeasureVmaf = work.Spec.Kind == MediaKind.Video,
                AudioLoudnessGateEnabled = false,
                AudioClippingGateEnabled = false,
                ImageQualityGateEnabled = false
            };
        }
        var clip = BuildVerificationClip(
            work.IsDisposable,
            work.IsCalibration,
            work.Spec.Kind,
            work.Spec.ClipSeconds,
            work.Spec.ClipStartSeconds,
            outputPath);
        // The VMAF pass is the long part of verification; surface its live progress on the same
        // job.Progress + SignalR channel the transcode uses. The reader already throttles to ~1%
        // steps, and both helpers serialise (job lock / hub), so fire-and-forget is safe here.
        var qualityProgress = new Progress<double>(fraction =>
        {
            _ = UpdateProgressAsync(jobId, fraction);
            _ = BroadcastProgressAsync(jobId, fraction, null, null, null);
        });
        // Mark verification as active work so the metrics broadcaster keeps sampling CPU load
        // (the VMAF pass runs its own ffmpeg outside the encode registry).
        VerificationOutcome outcome;
        using (remoteEvidence is null ? encodes.TrackVerification() : null)
        {
            var vmafAcceleration = VmafAccelerationSelector.Select(
                work.VideoEncoder,
                work.UsedHardwareDecode);
            outcome = await verification.VerifyAsync(
                work.Original,
                outputPath,
                policy,
                cancellationToken,
                clip,
                qualityProgress,
                vmafAcceleration,
                remoteQuality,
                remoteEvidence);
        }
        outcome = outcome with
        {
            Report = outcome.Report with
            {
                Context = new VerificationContext(
                    work.VideoEncoder,
                    work.VideoQuality?.Requested,
                    work.VideoQuality?.Effective,
                    work.VideoQuality?.Mode,
                    work.VideoQuality?.RetryCount ?? 0,
                    outcome.VmafSampling,
                    policy.MinimumVmafHarmonicMean,
                    policy.MinimumVmafMin,
                    policy.MinimumVmafCatastrophicMin,
                    work.SoftwareDecodeRetryReason,
                    remoteEvidence is null ? "Server" : "Worker")
            }
        };
        var reportJson = JsonSerializer.Serialize(outcome.Report, ReportJsonOptions);

        await WithJobAsync(jobId, job =>
        {
            job.OutputSizeBytes = outcome.OutputSizeBytes;
            job.VerificationReportJson = reportJson;
            job.VerificationPassed = outcome.Report.Passed;
            job.CalibrationReferenceStartSeconds = work.IsCalibration
                ? outcome.ReferenceStartSeconds
                : null;
            job.VerifiedAt = DateTimeOffset.UtcNow;
            job.UpdatedAt = DateTimeOffset.UtcNow;
        }, CancellationToken.None);

        if (!outcome.Report.Passed)
        {
            var sourceFailure = HardwareDecodeFallback.SourceFailureBlockingRetry(outcome.Report);
            if (sourceFailure is not null)
            {
                logger.LogWarning(
                    "Job {JobId}: retry suppressed because unchanged source check {FailedGate} failed",
                    jobId, sourceFailure);
            }
            // Asked before the higher-quality retry: a corrupt decode fails at every quality, so
            // re-encoding it at a higher one would only spend a second encode on the same rubbish.
            if (softwareDecodeRetryAvailable
                && !work.IsDisposable
                && HardwareDecodeFallback.ShouldRetryAfterVerification(
                    outcome.Report,
                    policy.MinimumVmafCatastrophicMin))
            {
                logger.LogWarning(
                    "Job {JobId}: the hardware-decoded output failed verification with the signature of "
                    + "decoder corruption ({Failures}); re-encoding with software decode",
                    jobId,
                    string.Join("; ", outcome.Report.Checks
                        .Where(check => check.Outcome == CheckOutcome.Failed)
                        .Select(check => check.Name)));
                return VerificationDisposition.RetryWithSoftwareDecode;
            }

            if (sourceFailure is null && !work.IsDisposable && VmafRetryPolicy.ShouldRetry(
                    outcome.Report,
                    work.VideoQuality?.RetryCount ?? 0,
                    work.VideoQuality?.Effective))
            {
                await QueueHigherQualityRetryAsync(jobId, outputPath);
                return VerificationDisposition.Finished;
            }

            var failed = outcome.Report.Checks
                .Where(check => check.Outcome == CheckOutcome.Failed)
                .ToList();
            var summary = "Verification failed: " + string.Join("; ", failed.Select(check => check.Name));
            if (sourceFailure is not null)
            {
                summary += $". Retry skipped: {sourceFailure} failed on the unchanged source.";
            }
            logger.LogWarning(
                "Job {JobId} verification failed: {Failures}",
                jobId,
                string.Join("; ", failed.Select(check => $"{check.Name}: {check.Detail}")));
            await CompleteAsync(
                jobId,
                JobStatus.Failed,
                error: summary,
                immediateAutoExclusion: AutoExclusionPolicy.ImmediateReason(
                    outcome.Report,
                    work.VideoQuality?.RetryCount ?? 0,
                    work.VideoQuality?.Effective));
            await NotifyJobFailedAsync(jobId, summary);
            return VerificationDisposition.Finished;
        }

        if (work.IsDisposable)
        {
            await CompleteAsync(jobId, JobStatus.Completed, progress: 1.0);
            return VerificationDisposition.Finished;
        }

        await FinishSuccessfulJobAsync(jobId, outputPath, work, settings.DryRunMode);
        return VerificationDisposition.Finished;
    }

    internal static VerificationClip? BuildVerificationClip(
        bool isDisposable,
        bool isCalibration,
        MediaKind kind,
        int? clipSeconds,
        int? clipStartSeconds,
        string outputPath) =>
        isDisposable && clipSeconds is { } seconds
            ? new VerificationClip(
                seconds,
                clipStartSeconds,
                Path.Combine(
                    Path.GetDirectoryName(outputPath)!,
                    kind == MediaKind.Audio
                        ? ".optimisarr-comparison-reference.flac"
                        : ".optimisarr-comparison-reference.mkv"),
                RetainReference: isCalibration,
                // Candidate files still encode the complete preset. Only the unchanged original
                // reference is video-only, which gives verification an exact picture window and
                // enables the frame-alignment probe for long-GOP sources.
                VideoOnly: isCalibration && kind == MediaKind.Video)
            : null;

    private async Task QueueHigherQualityRetryAsync(int jobId, string outputPath)
    {
        DeleteWorkOutput(outputPath);
        await WithJobAsync(jobId, job =>
        {
            job.Status = JobStatus.Queued;
            job.QualityRetryCount += 1;
            job.Progress = 0;
            job.ErrorMessage = null;
            job.FailureCategory = null;
            job.ProcessLog = null;
            job.WorkOutputPath = null;
            job.OutputSizeBytes = null;
            job.VerificationPassed = null;
            job.VerificationReportJson = null;
            job.VerifiedAt = null;
            job.FinishedAt = null;
            job.UpdatedAt = DateTimeOffset.UtcNow;
        }, CancellationToken.None);
        logger.LogInformation(
            "Job {JobId}: VMAF was the only failed gate; queued one deterministic higher-quality retry.",
            jobId);
        await NotifyAsync();
        Wake();
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
            WorkPaths.PruneEmptyAncestors(_workRoot, outputPath, IsWorkDirectoryReserved);

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
            var guarded = await pauseManager.TryRunAutomaticActionAsync(async () =>
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var replacement = scope.ServiceProvider.GetRequiredService<ReplacementService>();
                return await replacement.ReplaceAsync(jobId, CancellationToken.None);
            }, CancellationToken.None);
            if (!guarded.Started)
            {
                return;
            }

            var result = guarded.Value!;
            if (result.Kind == ReplacementResultKind.Success)
            {
                logger.LogInformation("Job {JobId}: auto-replaced the original (library auto-replace).", jobId);
            }
            else if (result.Kind == ReplacementResultKind.AlreadyCompleted)
            {
                logger.LogDebug(
                    "Job {JobId}: auto-replace observed a replacement already completed by another caller.",
                    jobId);
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
        var limits = settings.EffectiveWorkloadSlots(Environment.ProcessorCount,
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
        var running = RunningSlots();
        var activity = await activityMonitor.GetActivityAsync(cancellationToken);
        var decision = EvaluateDispatchPolicy(settings, activity);
        var freeDiskBytes = WorkPaths.TryGetAvailableFreeSpace(_workRoot);

        // When dispatch is otherwise ready but nothing runs, explain whether the backlog is just
        // waiting for closed per-library windows (the common "why isn't it running?" surprise).
        var waitingReason = decision.CanStart && _running.Count == 0
            ? await DescribeWindowWaitAsync(cancellationToken)
            : null;

        var lanes = await GetWorkloadLanesAsync(settings, limits, running, decision,
            waitingReason, cancellationToken);

        var pause = pauseManager.Snapshot;
        return new QueueDispatchStatus(
            decision.CanStart,
            decision.BlockedReason,
            pause.IsPaused,
            pause.Mode,
            pause.RunningEncodesSuspended,
            pause.SuspendedEncodeCount,
            pause.FailedEncodeCount,
            _running.Count,
            settings.MaxConcurrentJobs,
            settings.MinFreeDiskBytes,
            settings.CpuThreadLimit,
            settings.EncoderMode,
            encodes.AnyHardware,
            freeDiskBytes,
            _workRoot,
            waitingReason,
            lanes);
    }

    private async Task<IReadOnlyList<WorkloadLaneStatus>> GetWorkloadLanesAsync(
        QueueSettings settings, WorkloadSlots limits, WorkloadSlots running,
        DispatchDecision decision, string? waitingReason, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var queued = await db.Jobs.AsNoTracking()
            .Where(job => job.Status == JobStatus.Queued)
            .Select(job => new { job.Id, job.LibraryId, job.Type, job.EnqueuedAt,
                Kind = job.MediaFile != null ? job.MediaFile.MediaKind : MediaKind.Unknown })
            .ToListAsync(cancellationToken);
        var delivered = await DeliveredWorkloadsAsync(db, cancellationToken);
        var libraries = await db.Libraries.AsNoTracking()
            .Select(library => new { library.Id, library.WorkPlacement, library.AutoEnqueueEnabled,
                library.AutoEnqueueWindowStart, library.AutoEnqueueWindowEnd })
            .ToDictionaryAsync(library => library.Id, library => library, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var availability = await WorkerAvailability.ResolveAsync(db, settings.RemoteWorkersEnabled,
            scope.ServiceProvider.GetRequiredService<RemoteWorkersFeature>(), now, cancellationToken);
        var remoteWorkersOn = availability.RemoteWorkersOn;
        var scheduled = queued.Select(job => new QueuedJob(job.Id, job.LibraryId, 0,
            job.EnqueuedAt, IgnoreLibraryWindow: job.Type == JobType.Preview,
            Placement: job.Type == JobType.Normal && job.LibraryId is { } id
                && libraries.TryGetValue(id, out var library) ? library.WorkPlacement : WorkPlacement.Anywhere,
            Kind: job.Kind)).ToList();
        var autoWindows = libraries.Values.Where(library => library.AutoEnqueueEnabled)
            .ToDictionary(library => library.Id,
                library => (library.AutoEnqueueWindowStart, library.AutoEnqueueWindowEnd));
        var withinWindow = JobScheduler.WithinLibraryWindows(scheduled, autoWindows,
            TimeOnly.FromDateTime(DateTime.Now));
        // Use the same first-runnable clock and placement policy as dispatch. Counting every
        // PreferWorker job as local made the lane cards claim there was local work ready while the
        // scheduler was correctly reserving it for a sidecar.
        var local = SelectLocallyRunnable(withinWindow, _firstRunnableAt,
            remoteWorkersOn, _ => availability.AWorkerCouldTakeWork, now);
        var localIds = local.Select(job => job.Id).ToHashSet();
        var workerWaiting = withinWindow.Count(job => !localIds.Contains(job.Id));
        var videoWaiting = local.Count(job => JobScheduler.LaneFor(job.Kind) == WorkloadLane.Video)
            + delivered.Count(job => job.Lane == WorkloadLane.Video);
        var nonVideoWaiting = local.Count(job => JobScheduler.LaneFor(job.Kind) == WorkloadLane.NonVideo);
        var evidenceWaiting = delivered.Count(job => job.Lane == WorkloadLane.Evidence);
        var workers = remoteWorkersOn
            ? await db.Workers.AsNoTracking()
                .Where(worker => worker.RevokedAt == null && worker.DrainRequestedAt == null)
                .Select(worker => new { worker.MaxConcurrency, worker.LastSeenAt })
                .ToListAsync(cancellationToken)
            : [];
        var remoteCapacity = workers.Where(worker => WorkerLiveness.IsOnline(worker.LastSeenAt, now))
            .Sum(worker => Math.Max(0, worker.MaxConcurrency));
        var remoteActive = remoteWorkersOn
            ? await db.Jobs.AsNoTracking().CountAsync(job => job.Status == JobStatus.Leased, cancellationToken)
            : 0;
        var finalisation = scope.ServiceProvider.GetRequiredService<ReplacementCoordinator>();
        string? Reason(int waiting, int active, int capacity, string label) =>
            waiting == 0 ? null : !decision.CanStart ? decision.BlockedReason
            : capacity == 0 ? $"No extra {label} slots are configured. These jobs can use a free video slot."
            : active >= capacity ? $"All {label} slots are busy."
            : waitingReason ?? "Eligible jobs are being considered for the next dispatch.";
        return
        [
            new("Video", running.Video, limits.Video, videoWaiting,
                Reason(videoWaiting, running.Video, limits.Video, "video")),
            new("NonVideo", running.NonVideo, limits.NonVideo, nonVideoWaiting,
                Reason(nonVideoWaiting, running.NonVideo, limits.NonVideo, "non-video")),
            new("Evidence", running.Evidence, limits.Evidence, evidenceWaiting,
                Reason(evidenceWaiting, running.Evidence, limits.Evidence, "evidence validation")),
            new("Finalization", finalisation.Active, ReplacementCoordinator.Capacity, finalisation.Waiting,
                finalisation.Waiting > 0 ? "Both finalisation slots are busy; this source is waiting for a safe replacement turn." : null),
            new("Workers", remoteActive, remoteCapacity, workerWaiting,
                workerWaiting == 0 ? null : waitingReason ?? (remoteCapacity == 0 ? "No accepting worker is online."
                    : remoteActive >= remoteCapacity ? "All worker slots are busy."
                    : "Waiting for an eligible worker to claim these jobs."))
        ];
    }

    private async Task<string?> DescribeWindowWaitAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();

        var queuedByLibrary = await db.Jobs
            .AsNoTracking()
            .Where(job => job.Type == JobType.Normal && job.Status == JobStatus.Queued)
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
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();

        // This is a normal-queue operation. Keep interactive preview/calibration work owned by its
        // panel; otherwise a queue cleanup would silently strand a hidden comparison session.
        var runningJobIds = await db.Jobs
            .Where(job => job.Type == JobType.Normal
                && (job.Status == JobStatus.Probing
                    || job.Status == JobStatus.Transcoding
                    || job.Status == JobStatus.Verifying))
            .Select(job => job.Id)
            .ToListAsync(cancellationToken);
        foreach (var jobId in runningJobIds)
        {
            RequestCancel(jobId);
        }

        // A delivered candidate that has not been verified is pending work too: clearing the queue
        // discards it along with the rest, since nothing has been earned by it yet.
        var pending = await db.Jobs
            .Where(job => job.Type == JobType.Normal
                && (job.Status == JobStatus.Queued
                    || job.Status == JobStatus.AwaitingVerification
                    || job.Status == JobStatus.AwaitingSizeReview
                    || job.Status == JobStatus.ReadyToReplace))
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

    /// <summary>
    /// Manually pauses the queue: no new dispatch or automatic replacement starts, and supported
    /// platforms suspend running transcodes without losing progress. Verification already underway
    /// is allowed to finish and is disclosed in the returned status.
    /// </summary>
    public async Task PauseQueueAsync(CancellationToken cancellationToken)
    {
        await pauseManager.PauseAsync(cancellationToken);
        logger.LogInformation("Queue paused by the operator: {Reason}", pauseManager.Snapshot.BlockedReason);
        await NotifyAsync();
    }

    /// <summary>Resumes suspended encodes and only then reopens durable queue dispatch.</summary>
    public async Task<QueueResumeResult> ResumeQueueAsync(CancellationToken cancellationToken)
    {
        var result = await pauseManager.ResumeAsync(cancellationToken);
        await NotifyAsync();
        if (result.Resumed)
        {
            logger.LogInformation("Queue resumed by the operator.");
            Wake();
        }
        return result;
    }

    private async Task<QueueSettings> GetQueueSettingsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<SettingsStore>();
        return await settings.GetQueueSettingsAsync(cancellationToken);
    }

    private DispatchDecision EvaluateDispatchPolicy(
        QueueSettings settings,
        ActivityDecision activity,
        bool ignoreServicesActivity = false) =>
        DispatchPolicyEvaluator.Evaluate(
            settings.MinFreeDiskBytes,
            WorkPaths.TryGetAvailableFreeSpace(_workRoot),
            activity.Active,
            activity.Reason,
            ignoreServicesActivity,
            pauseManager.IsPaused,
            pauseManager.Snapshot.BlockedReason);

    private async Task<EncoderSelection> ResolveVideoEncoderAsync(
        string? targetCodec,
        EncoderMode encoderMode,
        int? sourceBitDepth,
        CancellationToken cancellationToken)
    {
        if (targetCodec is null)
        {
            return EncoderSelection.Success("copy");
        }

        var detected = await hardware.DetectAsync(cancellationToken);
        return EncoderSelector.Select(targetCodec, encoderMode, detected.Encoders, sourceBitDepth);
    }

    // A hardware encoder is named after its API (e.g. hevc_qsv, h264_vaapi, hevc_nvenc,
    // hevc_videotoolbox); the software libraries (libx265, libx264, libsvtav1) are not. Used to
    // flag GPU-backed work.
    private static bool IsHardwareEncoder(string? encoder) =>
        encoder is not null
        && (encoder.EndsWith("_qsv", StringComparison.OrdinalIgnoreCase)
            || encoder.EndsWith("_vaapi", StringComparison.OrdinalIgnoreCase)
            || encoder.EndsWith("_nvenc", StringComparison.OrdinalIgnoreCase)
            || encoder.EndsWith("_videotoolbox", StringComparison.OrdinalIgnoreCase));

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
                .Where(job => job.Id == jobId
                    && job.Type == JobType.Normal
                    && job.MediaFile != null)
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
    private Task BroadcastProgressAsync(
        int jobId,
        double progress,
        double? fps,
        double? speed,
        double? etaSeconds,
        bool finishing = false) =>
        hub.Clients.All.SendAsync("jobProgress", new { jobId, progress, fps, speed, etaSeconds, finishing });

    /// <summary>
    /// Deletes a persisted scratch output before its job row is retried or removed. Paths outside
    /// the owned work root (for example a completed move-to-folder output) are deliberately ignored.
    /// A false result means the row must be retained so the output never becomes an untracked orphan.
    /// </summary>
    public bool TryDiscardWorkOutput(string? path) => DeleteWorkOutput(path);

    // Held for as long as FFmpeg is writing into the directory. Reference counted, because two
    // concurrent jobs on the same media file legitimately share one.
    private IDisposable ReserveWorkDirectory(string directory)
    {
        var key = NormaliseDirectory(directory);
        _reservedWorkDirectories.AddOrUpdate(key, 1, static (_, count) => count + 1);
        return new WorkDirectoryReservation(_reservedWorkDirectories, key);
    }

    /// <summary>
    /// Which of the jobs eligible to run right now this machine may take itself.
    ///
    /// Separated from the dispatch loop because the bug it fixes is invisible in the policy it
    /// calls: <see cref="WorkPlacementPolicy.MayRunLocally"/> was always correct, and was simply
    /// being handed the wrong instant. Passing the moment a job became runnable, rather than the
    /// moment it was enqueued, is the whole of the fix, and it is only testable if the choice of
    /// instant lives somewhere a test can reach.
    /// </summary>
    /// <summary>
    /// True when no worker can be offered this job yet, because its library chooses a per-title
    /// quality and this job has not been given one. The search runs here, so until it has, the job
    /// belongs to this machine whatever the placement says.
    /// </summary>
    internal static bool AwaitsLocalQualityChoice(QueuedJob job, IReadOnlySet<int> adaptiveLibraryIds) =>
        !job.QualityChosen && job.LibraryId is { } libraryId && adaptiveLibraryIds.Contains(libraryId);

    internal static List<QueuedJob> SelectLocallyRunnable(
        IReadOnlyList<QueuedJob> withinWindow,
        IReadOnlyDictionary<int, DateTimeOffset> firstRunnableAt,
        bool remoteWorkersEnabled,
        Func<QueuedJob, bool> aWorkerCouldTakeIt,
        DateTimeOffset nowUtc) =>
        withinWindow
            .Where(job => WorkPlacementPolicy.MayRunLocally(
                job.Placement,
                remoteWorkersEnabled,
                aWorkerCouldTakeIt(job),
                // Never the enqueue time. A job parked outside its library's window was not being
                // offered to anybody, so counting that wait against a worker's head start hands
                // the work straight to this machine the instant the window opens.
                firstRunnableAt.TryGetValue(job.Id, out var runnableSince) ? runnableSince : nowUtc,
                nowUtc))
            .ToList();

    /// <summary>
    /// Forgets jobs that have left the queue, so a long-running server does not accumulate a
    /// timestamp for every job it has ever dispatched.
    /// </summary>
    private void PruneFirstRunnable(IReadOnlyCollection<QueuedJob> queued)
    {
        if (_firstRunnableAt.IsEmpty)
        {
            return;
        }

        var stillQueued = queued.Select(job => job.Id).ToHashSet();
        foreach (var id in _firstRunnableAt.Keys)
        {
            if (!stillQueued.Contains(id))
            {
                _firstRunnableAt.TryRemove(id, out _);
            }
        }
    }

    private bool IsWorkDirectoryReserved(string directory) =>
        _reservedWorkDirectories.ContainsKey(NormaliseDirectory(directory));

    private static string NormaliseDirectory(string directory)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException)
        {
            return directory;
        }
    }

    private sealed class WorkDirectoryReservation(
        ConcurrentDictionary<string, int> reservations,
        string key) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released)
            {
                return;
            }

            _released = true;
            while (reservations.TryGetValue(key, out var count))
            {
                if (count <= 1)
                {
                    if (reservations.TryRemove(new KeyValuePair<string, int>(key, count)))
                    {
                        return;
                    }
                }
                else if (reservations.TryUpdate(key, count - 1, count))
                {
                    return;
                }
            }
        }
    }

    private bool DeleteWorkOutput(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return true;
        }

        if (!WorkPaths.IsUnderRoot(_workRoot, path))
        {
            return true;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not delete work output {Path}", path);
            return false;
        }

        // Tidy the per-media-file scratch directory this output lived in so /work does not
        // accumulate an empty tree for every file ever processed.
        WorkPaths.PruneEmptyAncestors(_workRoot, path, IsWorkDirectoryReserved);
        return true;
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

}

/// <summary>The ffmpeg binary used for transcoding (see OPTIMISARR_FFMPEG).</summary>
public sealed record TranscodeOptions(string Ffmpeg);

public sealed record QueueDispatchStatus(
    bool CanStart,
    string? BlockedReason,
    // True only for the operator's manual pause, so the UI can offer Resume — the automatic
    // gates (playback, low disk) clear themselves and must not.
    bool ManuallyPaused,
    string ManualPauseMode,
    bool RunningEncodesSuspended,
    int SuspendedEncodeCount,
    int PauseFailedEncodeCount,
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
    string? WaitingReason,
    IReadOnlyList<WorkloadLaneStatus>? WorkloadLanes = null);

public sealed record WorkloadLaneStatus(string Lane, int Active, int Capacity, int Waiting, string? Reason);
