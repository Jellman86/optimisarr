using System.Text.Json;
using System.Text.Json.Serialization;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Verification;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Library;
using Optimisarr.Core.Library;
using Optimisarr.Api.Deduplication;
using Optimisarr.Api.Queue;
using Optimisarr.Api.Stats;
using Optimisarr.Core.IO;
using Optimisarr.Core.Replacement;
using Optimisarr.Data;
using ReplacementEntity = Optimisarr.Data.Replacement;

namespace Optimisarr.Api.Replacement;

public enum ReplacementResultKind
{
    Success,
    AlreadyCompleted,
    NotFound,
    Invalid,

    /// <summary>
    /// A library rule correctly declined this replacement for now, and the same job may succeed
    /// later — the file is hardlinked today but need not be tomorrow. Distinct from Invalid so
    /// automatic reconciliation, which retries every few seconds, can stay quiet about an expected
    /// decline instead of logging a warning forever.
    /// </summary>
    Deferred,
    Failed
}

public sealed record ReplacementActionResult(
    ReplacementResultKind Kind,
    string? Message,
    ReplacementEntity? Replacement,
    // A permanent failure can never succeed on a later retry (the verified output is gone, the
    // original is gone, or a different optimised file permanently occupies the destination). The
    // dispatcher fails such a job once rather than reconciling it on every cycle forever.
    bool Permanent = false,
    [property: System.Text.Json.Serialization.JsonIgnore] bool FailureAlreadyRecorded = false)
{
    public static ReplacementActionResult Ok(ReplacementEntity replacement) =>
        new(ReplacementResultKind.Success, null, replacement);

    public static ReplacementActionResult AlreadyCompleted(ReplacementEntity replacement) =>
        new(ReplacementResultKind.AlreadyCompleted, null, replacement);

    public static ReplacementActionResult NotFound(string message) =>
        new(ReplacementResultKind.NotFound, message, null);

    public static ReplacementActionResult Invalid(string message) =>
        new(ReplacementResultKind.Invalid, message, null);

    public static ReplacementActionResult Deferred(string message) =>
        new(ReplacementResultKind.Deferred, message, null);

    public static ReplacementActionResult Failed(string message, bool permanent = false, bool failureAlreadyRecorded = false) =>
        new(ReplacementResultKind.Failed, message, null, permanent, failureAlreadyRecorded);
}

public sealed record BulkReplacementFailure(int JobId, string Message);

public sealed record BulkReplacementResult(
    int Attempted,
    int Replaced,
    IReadOnlyList<BulkReplacementFailure> Failures);

/// <summary>
/// Performs the only destructive step in Optimisarr — putting a verified output in
/// place of an original — and makes it reversible. The original is moved to
/// quarantine <em>first</em>, then the output is moved into place; a recorded
/// <see cref="Data.Replacement"/> is the rollback path. If any step fails the
/// original is restored when paths are unambiguous; otherwise its quarantine and durable recovery record are retained.
/// </summary>
public sealed class ReplacementService
{
    private readonly OptimisarrDbContext _db;
    private readonly LibraryInventoryService _inventory;
    private readonly ILogger<ReplacementService> _logger;
    private readonly SettingsStore _settings;
    private readonly string _trashRoot;
    private readonly string? _workRoot;
    private readonly Func<string, string, bool> _canMoveAtomically;
    private readonly Func<string, string, FileMoveResult> _moveFile;
    private readonly LibraryRefreshService? _refresh;
    private readonly NotificationService? _notifications;
    private readonly LifetimeStatsStore _lifetime;
    private readonly ReplacementCoordinator _coordinator;

    public ReplacementService(
        OptimisarrDbContext db,
        LibraryInventoryService inventory,
        SettingsStore settings,
        IHostEnvironment environment,
        LibraryRefreshService refresh,
        NotificationService notifications,
        ReplacementCoordinator coordinator,
        ILogger<ReplacementService> logger)
        : this(db, inventory, settings, TrashPaths.Resolve(environment), logger,
            refresh: refresh, notifications: notifications, workRoot: WorkPaths.Resolve(environment),
            coordinator: coordinator)
    {
    }

    // Test seam: lets the suite point the trash root at a temp directory. The library
    // refresh and notifications are optional so tests need not stand up an HTTP stack;
    // moveFile lets a test simulate a mid-move failure to exercise the restore path. The
    // coordinator defaults to a private instance so a test that does not exercise concurrency
    // need not supply one.
    internal ReplacementService(
        OptimisarrDbContext db,
        LibraryInventoryService inventory,
        SettingsStore settings,
        string trashRoot,
        ILogger<ReplacementService> logger,
        Func<string, string, bool>? canMoveAtomically = null,
        LibraryRefreshService? refresh = null,
        NotificationService? notifications = null,
        string? workRoot = null,
        Func<string, string, FileMoveResult>? moveFile = null,
        ReplacementCoordinator? coordinator = null)
    {
        _db = db;
        _inventory = inventory;
        _settings = settings;
        _trashRoot = trashRoot;
        _workRoot = workRoot;
        _logger = logger;
        _canMoveAtomically = canMoveAtomically ?? FileMover.CanMoveAtomically;
        _moveFile = moveFile ?? FileMover.Move;
        _refresh = refresh;
        _notifications = notifications;
        _lifetime = new LifetimeStatsStore(db);
        _coordinator = coordinator ?? new ReplacementCoordinator();
    }

    public async Task<BulkReplacementResult> ReplaceReadyAsync(CancellationToken cancellationToken)
    {
        var jobIds = await _db.Jobs
            .AsNoTracking()
            .Where(job => job.Type == JobType.Normal
                && job.Status == JobStatus.ReadyToReplace
                && job.VerificationPassed == true)
            .OrderBy(job => job.Id)
            .Select(job => job.Id)
            .ToListAsync(cancellationToken);

        var replaced = 0;
        var failures = new List<BulkReplacementFailure>();
        foreach (var jobId in jobIds)
        {
            ReplacementActionResult result;
            try
            {
                result = await ReplaceAsync(jobId, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected failure while replacing ready job {JobId}", jobId);
                _db.ChangeTracker.Clear();
                failures.Add(new BulkReplacementFailure(
                    jobId,
                    $"Unexpected replacement failure: {ex.Message}"));
                continue;
            }

            if (result.Kind is ReplacementResultKind.Success or ReplacementResultKind.AlreadyCompleted)
            {
                replaced++;
            }
            else
            {
                failures.Add(new BulkReplacementFailure(
                    jobId,
                    result.Message ?? "The job could not be replaced."));
            }
        }

        return new BulkReplacementResult(jobIds.Count, replaced, failures);
    }

    public Task<ReplacementActionResult> ReplaceAutomaticallyAsync(int jobId, CancellationToken cancellationToken) =>
        ReplaceGuardedAsync(jobId, ReplaceRequest.Automatic, cancellationToken);

    public Task<ReplacementActionResult> ReplaceAsync(int jobId, CancellationToken cancellationToken) =>
        ReplaceGuardedAsync(jobId, ReplaceRequest.Manual, cancellationToken);

    /// <summary>
    /// Replaces the original with a failed job's output whose only fault is a picture-quality score
    /// under the library's target, because a person looked at it and accepted it. Every other
    /// replacement safeguard still applies, and the original goes to quarantine as usual. The
    /// <paramref name="executionAttempt"/> is the attempt the person saw, so a job retried in the
    /// meantime is refused rather than accepted on someone else's evidence.
    /// </summary>
    public Task<ReplacementActionResult> AcceptBelowTargetAsync(int jobId, int executionAttempt, CancellationToken cancellationToken) =>
        ReplaceGuardedAsync(jobId, ReplaceRequest.BelowTarget(executionAttempt), cancellationToken);

    private sealed record ReplaceRequest(bool IsAutomatic, int? AcceptedAttempt)
    {
        public static readonly ReplaceRequest Manual = new(false, null);
        public static readonly ReplaceRequest Automatic = new(true, null);
        public static ReplaceRequest BelowTarget(int attempt) => new(false, attempt);
        public bool AcceptsBelowTarget => AcceptedAttempt is not null;
    }

    private async Task<ReplacementActionResult> ReplaceGuardedAsync(int jobId, ReplaceRequest request, CancellationToken cancellationToken)
    {
        // Only one replacement may act on a source at a time. A job becomes replaceable the instant it
        // reaches ReadyToReplace, where the post-verify auto-replace, the reconcile sweep, and a
        // manual replace can all target it at once; overlapping runs corrupt each other's moves and
        // destroy the verified output (the original is still safely restored). The loser of the claim
        // backs off and lets the winner finish.
        var source = await _db.Jobs.AsNoTracking().Where(job => job.Id == jobId)
            .Select(job => new { job.MediaFileId, job.MediaFile!.Path, job.WorkOutputPath }).FirstOrDefaultAsync(cancellationToken);
        if (source is null)
        {
            return ReplacementActionResult.NotFound($"No job with id {jobId}.");
        }
        var fileKeys = FileOperationClaims.Keys(source.MediaFileId, source.Path,
            source.WorkOutputPath ?? source.Path,
            source.WorkOutputPath is { } output ? Path.ChangeExtension(source.Path, Path.GetExtension(output)) : source.Path);
        if (!await _coordinator.TryBeginAsync(jobId, source.MediaFileId, cancellationToken, fileKeys))
        {
            return ReplacementActionResult.Invalid(
                $"A replacement or rollback for job {jobId} or its source is already in progress.");
        }

        try
        {
            if (await DedupeJournalStore.IsReservedAsync(_db, fileKeys, cancellationToken))
                return ReplacementActionResult.Deferred("An unresolved exact-copy action reserves this source or destination. Resolve its journal before replacement.");
            return await ReplaceCoreAsync(jobId, request, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ReplacementActionResult.Failed($"Replacement could not read or lock its files safely: {ex.Message}. The recorded original was kept.");
        }
        finally
        {
            _coordinator.End(jobId, source.MediaFileId);
        }
    }

    private async Task<ReplacementActionResult> RefuseIdentityAsync(Job job, string message, bool automatic, CancellationToken token)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        { Converters = { new JsonStringEnumConverter() } };
        var check = new VerificationCheck("File identity", CheckOutcome.Failed, message);
        var report = new VerificationReport([check]);
        if (job.VerificationReportJson is { } json)
        {
            try
            {
                var previous = JsonSerializer.Deserialize<VerificationReport>(json, options);
                if (previous is not null && previous.HasValidStructure())
                    report = previous with { Checks = [.. previous.Checks, check] };
            }
            catch (JsonException) { } // The new identity refusal is valid evidence on its own.
        }
        var reportJson = JsonSerializer.Serialize(report, options);
        var now = DateTimeOffset.UtcNow;
        // Make manual refusal retryable too. A concurrent cancel or newer attempt must not be
        // overwritten, and historical quality measurements remain alongside the failed gate.
        await using var write = await _db.BeginDiagnosticWriteAsync(token);
        var written = await _db.Jobs.Where(row => row.Id == job.Id && row.Status == JobStatus.ReadyToReplace
                && row.ExecutionAttempt == job.ExecutionAttempt)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.Status, JobStatus.Failed)
                .SetProperty(row => row.VerificationPassed, false)
                .SetProperty(row => row.VerificationReportJson, reportJson)
                .SetProperty(row => row.VerifiedSourceSha256, (string?)null)
                .SetProperty(row => row.VerifiedOutputSha256, (string?)null)
                .SetProperty(row => row.ErrorMessage, message)
                .SetProperty(row => row.FailureCategory, FailureCategory.Verification)
                .SetProperty(row => row.VerifiedAt, now)
                .SetProperty(row => row.FinishedAt, now)
                .SetProperty(row => row.UpdatedAt, now), token);
        await _db.Entry(job).ReloadAsync(token);
        if (written == 0)
            return ReplacementActionResult.Invalid("The job changed during the identity check. No replacement was made.");
        await DiagnosticEventCapture.AppendJobTransitionAsync(_db, job, JobStatus.ReadyToReplace, now, token);
        if (automatic) await QueueDispatcher.ApplyFailureTrackingAsync(_db, job, JobStatus.Failed);
        await _db.SaveChangesAsync(token);
        await write.CommitAsync(token);
        return ReplacementActionResult.Failed(message, permanent: true, failureAlreadyRecorded: true);
    }

    /// <summary>
    /// Null when this failed job may be accepted below its picture-quality target; otherwise the
    /// refusal. Uses the same rule the Queue shows the button by, against the job as it is now.
    /// </summary>
    private async Task<ReplacementActionResult?> RefuseBelowTargetAsync(
        Job job, MediaFile media, int acceptedAttempt, CancellationToken cancellationToken)
    {
        if (job.ExecutionAttempt != acceptedAttempt)
            return ReplacementActionResult.Invalid("This job has been tried again since you looked at it. No replacement was made.");
        var dryRun = (await _settings.GetQueueSettingsAsync(cancellationToken)).DryRunMode;
        var decision = BelowTargetEligibility.Decide(job, media.MediaKind, media.Library?.MoveOnComplete == true, dryRun);
        if (!decision.Eligible)
            return ReplacementActionResult.Invalid(BelowTargetEligibility.Explain(decision.Refusal));
        // A file someone excluded by hand is one they decided to leave alone. An automatic exclusion
        // is expected here: a quality miss at the highest setting excludes the file at once.
        if (await _db.Exclusions.AnyAsync(e => e.Path == media.Path && e.Source == ExclusionSource.Manual, cancellationToken))
            return ReplacementActionResult.Invalid("This file is excluded. Remove its exclusion from the library's Excluded tab before accepting it.");
        if (await _db.Replacements.AnyAsync(r => r.JobId == job.Id
                && JobClearing.LiveReplacementStatuses.Contains(r.Status), cancellationToken))
            return ReplacementActionResult.Invalid("This job already has a replacement in progress or waiting for recovery.");
        return null;
    }

    /// <summary>
    /// The files no longer match what was measured. Forget their identity so the job stops being
    /// offered for acceptance; trying the file again makes a fresh, measurable attempt.
    /// </summary>
    private async Task<ReplacementActionResult> RefuseChangedBelowTargetAsync(Job job, CancellationToken token)
    {
        await _db.Jobs.Where(row => row.Id == job.Id && row.Status == JobStatus.Failed
                && row.ExecutionAttempt == job.ExecutionAttempt)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.VerifiedSourceSha256, (string?)null)
                .SetProperty(row => row.VerifiedOutputSha256, (string?)null)
                .SetProperty(row => row.UpdatedAt, DateTimeOffset.UtcNow), token);
        await _db.Entry(job).ReloadAsync(token);
        return ReplacementActionResult.Failed(
            "The original or the output changed after they were measured, so the output was not accepted. The original was left untouched; try the file again.",
            permanent: true);
    }

    // An acceptance whose output never reached the original's place did not happen: the job is
    // still the plain failure it was, and can be accepted again.
    private static void ForgetUnfinishedAcceptance(Job? job)
    {
        if (job is { Status: JobStatus.Failed }) job.AcceptedBelowTargetAt = null;
    }

    // A failed job a person accepted is finished, not failed: it must not be counted among failures.
    private static void ClearFailureOnceAccepted(Job job)
    {
        if (job.AcceptedBelowTargetAt is null) return;
        job.ErrorMessage = null;
        job.FailureCategory = null;
    }

    private Task<bool> IsAutomaticAcceptanceEnabledAsync(int? libraryId, CancellationToken cancellationToken) =>
        // The same rule as AutoReplacePolicy.ReplacesAutomatically, written so the database can apply it.
        _db.Libraries.AsNoTracking().AnyAsync(
            library => library.Id == libraryId && library.AutoReplace && !library.MoveOnComplete, cancellationToken);

    private async Task<ReplacementActionResult> ReplaceCoreAsync(int jobId, ReplaceRequest request, CancellationToken cancellationToken)
    {
        var automatic = request.IsAutomatic;
        var job = await _db.Jobs
            .Include(j => j.MediaFile)
            .ThenInclude(f => f!.Library)
            .FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);

        if (job is null)
        {
            return ReplacementActionResult.NotFound($"No job with id {jobId}.");
        }

        // A post-verify replacement, the reconciliation sweep, and a manual request can all select
        // the same ready job before one of them acquires the per-job claim. If the winner has already
        // committed the replacement, replaying the request is a successful no-op rather than an
        // invalid operation. A rolled-back/pending record is not complete and remains invalid.
        if (job.Status == JobStatus.Completed
            && (job.VerificationPassed == true || request.AcceptsBelowTarget && job.AcceptedBelowTargetAt is not null))
        {
            var existing = await _db.Replacements
                .AsNoTracking()
                .Where(replacement => replacement.JobId == jobId)
                .OrderByDescending(replacement => replacement.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (existing?.Status is ReplacementStatus.Replaced or ReplacementStatus.Purged)
            {
                return ReplacementActionResult.AlreadyCompleted(existing);
            }
        }

        if (job.MediaFile is not { } media)
        {
            return ReplacementActionResult.Invalid($"Job {jobId} has no media file to replace.");
        }

        if (request.AcceptedAttempt is { } acceptedAttempt)
        {
            if (await RefuseBelowTargetAsync(job, media, acceptedAttempt, cancellationToken) is { } refusal)
                return refusal;
        }
        else if (job.Status != JobStatus.ReadyToReplace)
        {
            return ReplacementActionResult.Invalid(
                $"Job {jobId} is {job.Status}; only a ReadyToReplace job can replace the original.");
        }
        else if (job.VerificationPassed != true)
        {
            return ReplacementActionResult.Invalid(
                $"Job {jobId} has not passed verification; it cannot replace the original.");
        }

        if (automatic && !await IsAutomaticAcceptanceEnabledAsync(media.LibraryId, cancellationToken))
            return ReplacementActionResult.Deferred("Auto-accept is off for this library. The verified output is ready for manual replacement; the original was left untouched.");

        var settings = await _settings.GetQueueSettingsAsync(cancellationToken);
        if (settings.DryRunMode)
        {
            return ReplacementActionResult.Invalid(
                "Dry-run mode is enabled, so Optimisarr will stop after verification and leave originals untouched.");
        }

        if (string.IsNullOrEmpty(job.WorkOutputPath) || !File.Exists(job.WorkOutputPath))
        {
            return ReplacementActionResult.Failed(
                "The verified output is missing from the work directory.", permanent: true);
        }

        if (!File.Exists(media.Path))
        {
            return ReplacementActionResult.Failed(
                $"The original file no longer exists: {media.Path}", permanent: true);
        }

        if (!LibraryPathSafety.IsSafeFile(media.Path, media.Library?.Path))
            return ReplacementActionResult.Deferred(LibraryPathSafety.Refusal);

        // The link count recorded at scan time decided whether to *encode* this file. Whether to
        // *replace* it is a different question asked at a different moment, and a download client
        // can hardlink a file at any point in between. Re-read it live, against the file that is
        // about to be moved into quarantine, rather than trusting a number that may be hours old.
        if (media.Library is { ExcludeHardLinkedFiles: true })
        {
            var links = HardLinkProbe.CountLinks(media.Path);
            if (links is null)
            {
                return ReplacementActionResult.Deferred(
                    $"This library excludes hardlinked files, and the link count for {media.Path} could not be "
                    + "read, so the original was left untouched.");
            }

            if (links > 1)
            {
                // Deliberately not permanent. The encoded output stays verified and ready, and the
                // job can replace normally once the other link is gone — a file that stops being
                // seeded should not need re-encoding from scratch.
                return ReplacementActionResult.Deferred(
                    $"{media.Path} now has {links} names pointing at it, and this library excludes hardlinked "
                    + "files. Replacing it would change the other copies, so the original was left untouched.");
            }
        }

        var plan = ReplacementPlanner.Plan(
            media.Path,
            job.WorkOutputPath,
            _trashRoot,
            DateTimeOffset.UtcNow,
            $"job-{job.Id}");

        // A transcode that changes the container lands at a new path (e.g. photo.bmp -> photo.webp).
        // If a *different* file already occupies that path — typically another source that optimised
        // to the same name — replacing would overwrite it. Fail safely before quarantining anything,
        // so the original is left untouched. (An unchanged-container replacement lands back on the
        // original's own path, which is expected and not a collision.)
        if (!string.Equals(plan.FinalPath, media.Path, StringComparison.Ordinal) && File.Exists(plan.FinalPath))
        {
            return ReplacementActionResult.Failed(
                $"Replacement would collide with an existing file at {plan.FinalPath}. " +
                "Another optimised file already occupies that path, so the original was left untouched.",
                permanent: true);
        }

        // The inventory holds one row per path. A row whose file vanished but whose history keeps it
        // through every scan still owns its path, so landing a new file there could never be recorded.
        if (!string.Equals(plan.FinalPath, media.Path, StringComparison.Ordinal)
            && await IsInventoryPathOwnedByAnotherRecordAsync(media.Id, plan.FinalPath, cancellationToken))
        {
            return ReplacementActionResult.Failed(
                $"Replacement would land on {plan.FinalPath}, which the inventory still records for another file with job or "
                + "replacement history. The original was left untouched.",
                permanent: true);
        }

        // Replacement moves the original into quarantine and the optimised file into the
        // original's folder, so that folder must be writable by the container's user. Check up
        // front and fail with a clear, actionable message rather than a raw 500 mid-move — the
        // original is still untouched at this point.
        var mediaDirectory = Path.GetDirectoryName(media.Path) ?? string.Empty;
        if (!PathAccessProbe.CanWrite(mediaDirectory))
        {
            return ReplacementActionResult.Invalid(
                $"Optimisarr can't write to the library folder '{mediaDirectory}'. Replacement needs write "
                + "access to move the original into quarantine and the optimised file into its place. Give the "
                + "container's user (PUID/PGID) write permission to your media path, then try again. The original "
                + "was left untouched. (The Libraries page has a 'Test access' check that flags this in advance.)");
        }

        if (!settings.ReplacementAllowCrossFilesystem)
        {
            var originalDirectory = Path.GetDirectoryName(plan.OriginalPath) ?? string.Empty;
            var quarantineDirectory = Path.GetDirectoryName(plan.QuarantinePath) ?? _trashRoot;
            var outputDirectory = Path.GetDirectoryName(job.WorkOutputPath) ?? string.Empty;
            var finalDirectory = Path.GetDirectoryName(plan.FinalPath) ?? originalDirectory;

            if (!_canMoveAtomically(originalDirectory, quarantineDirectory)
                || !_canMoveAtomically(outputDirectory, finalDirectory))
            {
                return ReplacementActionResult.Invalid(
                    "Replacement would require a cross-filesystem copy-plus-delete move. Enable cross-filesystem replacement in Settings if this mount layout is intentional.");
            }
        }

        if (!LibraryPathSafety.IsSafeFile(media.Path, media.Library?.Path))
            return ReplacementActionResult.Deferred(LibraryPathSafety.Refusal);

        if (request.AcceptsBelowTarget
            && (!FileContentIdentity.IsHash(job.VerifiedSourceSha256) || !FileContentIdentity.IsHash(job.VerifiedOutputSha256)))
            return ReplacementActionResult.Invalid("This output has no verified file identity, so it cannot be accepted. Try the file again.");

        if (!FileContentIdentity.IsHash(job.VerifiedSourceSha256) || !FileContentIdentity.IsHash(job.VerifiedOutputSha256))
            return await RefuseIdentityAsync(job,
                "This historical output has no verified file identity. The original was left untouched; retry to create a fresh verified attempt.", automatic, cancellationToken);

        await using var sourceGuard = FileContentIdentity.OpenGuard(media.Path);
        await using var outputGuard = FileContentIdentity.OpenGuard(job.WorkOutputPath);
        if (!await FileContentIdentity.MatchesAsync(media.Path, job.VerifiedSourceSha256, cancellationToken)
            || !await FileContentIdentity.MatchesAsync(job.WorkOutputPath, job.VerifiedOutputSha256, cancellationToken))
            return request.AcceptsBelowTarget
                ? await RefuseChangedBelowTargetAsync(job, cancellationToken)
                : await RefuseIdentityAsync(job,
                "Source or output bytes changed after verification. The original was left untouched; create a fresh verified attempt.", automatic, cancellationToken);

        var originalSize = new FileInfo(media.Path).Length;
        var outputSize = new FileInfo(job.WorkOutputPath).Length;

        // File identity checks can take time. Re-read the saved opt-in before recording or moving
        // anything so a setting cached when encoding began cannot authorise automatic replacement.
        if (automatic && !await IsAutomaticAcceptanceEnabledAsync(media.LibraryId, cancellationToken))
            return ReplacementActionResult.Deferred("Auto-accept was switched off while preparing this replacement. The original and verified output were left untouched.");

        // Record the rollback path durably before the first filesystem mutation. If the process
        // stops at any later instruction, startup recovery can restore the quarantined original or
        // finalize a completed pair of moves from this Pending row.
        if (!LibraryPathSafety.IsSafeFile(media.Path, media.Library?.Path))
            return ReplacementActionResult.Deferred(LibraryPathSafety.Refusal);

        // Mark the acceptance in the same write as the rollback record, and only if the job is still the
        // failed attempt the person looked at: a retry or another acceptance in between wins.
        await using var intent = await _db.BeginDiagnosticWriteAsync(cancellationToken);
        if (request.AcceptedAttempt is { } attempt)
        {
            var acceptedAt = DateTimeOffset.UtcNow;
            var marked = await _db.Jobs
                .Where(row => row.Id == job.Id && row.Status == JobStatus.Failed && row.ExecutionAttempt == attempt
                    && row.AcceptedBelowTargetAt == null)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.AcceptedBelowTargetAt, acceptedAt), cancellationToken);
            if (marked == 0)
                return ReplacementActionResult.Invalid("The job changed while it was being accepted. No replacement was made.");
            await _db.Entry(job).ReloadAsync(cancellationToken);
        }

        var replacement = new ReplacementEntity
        {
            JobId = job.Id,
            MediaFileId = media.Id,
            OriginalPath = plan.OriginalPath,
            QuarantinePath = plan.QuarantinePath,
            FinalPath = plan.FinalPath,
            OriginalSizeBytes = originalSize,
            NewSizeBytes = outputSize,
            OriginalSha256 = job.VerifiedSourceSha256,
            OutputSha256 = job.VerifiedOutputSha256,
            Status = ReplacementStatus.Pending,
            ReplacedAt = DateTimeOffset.UtcNow
        };
        _db.Replacements.Add(replacement);
        await _db.SaveChangesAsync(cancellationToken);
        await intent.CommitAsync(cancellationToken);

        try
        {
            if (!LibraryPathSafety.IsSafeFile(media.Path, media.Library?.Path))
                throw new IOException(LibraryPathSafety.Refusal);
            // Step 1: preserve the original by quarantining it. From here the original is safe and
            // every ordinary failure restores it; a process crash is handled by Pending recovery.
            var quarantineMove = _moveFile(media.Path, plan.QuarantinePath);

            if (!await FileContentIdentity.MatchesAsync(plan.QuarantinePath, replacement.OriginalSha256, cancellationToken))
                throw new IOException("The source changed at the quarantine boundary; the replacement was refused.");

            if (File.Exists(plan.OriginalPath))
                throw new IOException("A new file appeared at the original path after quarantine. Both originals are preserved; inspect the pending rollback before retrying.");

            // Step 2: move the verified output into the original's place.
            if (!LibraryPathSafety.IsSafeFile(plan.FinalPath, media.Library?.Path, allowMissingLeaf: true))
                throw new IOException(LibraryPathSafety.Refusal);
            var move = _moveFile(job.WorkOutputPath, plan.FinalPath);
            replacement.CrossFilesystem = quarantineMove.CrossFilesystem || move.CrossFilesystem;

            // Step 3: a final-path integrity check — the placed file must exist and
            // match the output we verified, or we do not trust the replacement.
            if (!File.Exists(plan.FinalPath) || new FileInfo(plan.FinalPath).Length != outputSize
                || !await FileContentIdentity.MatchesAsync(plan.FinalPath, replacement.OutputSha256, cancellationToken)
                || !await FileContentIdentity.MatchesAsync(plan.QuarantinePath, replacement.OriginalSha256, cancellationToken))
            {
                throw new IOException("The replaced file is missing or its size does not match the verified output.");
            }

            if (!await TryClaimInventoryPathAsync(media.Id, plan.FinalPath, cancellationToken))
                throw new IOException($"The inventory already records another file with history at {plan.FinalPath}.");
        }
        catch (Exception ex)
        {
            var restored = await RestoreFromQuarantineAsync(plan, media.Path, replacement.OutputSha256, replacement.OriginalSha256, media.Id);
            if (restored)
            {
                _db.Replacements.Remove(replacement);
                // The original is back, so nothing was accepted; the job stays a plain failure.
                ForgetUnfinishedAcceptance(job);
                await _db.SaveChangesAsync(CancellationToken.None);
            }

            _logger.LogError(
                ex,
                restored
                    ? "Replacement of job {JobId} failed; original restored from quarantine"
                    : "Replacement of job {JobId} failed; pending rollback record retained for startup recovery",
                jobId);
            var recovery = restored
                ? "the original was restored"
                : "the original remains protected by the recorded pending rollback path";
            return ReplacementActionResult.Failed($"Replacement failed and {recovery}: {ex.Message}");
        }

        replacement.Status = ReplacementStatus.Replaced;

        media.Path = plan.FinalPath;
        media.SizeBytes = outputSize;
        media.UpdatedAt = DateTimeOffset.UtcNow;

        job.Status = JobStatus.Completed;
        job.UpdatedAt = DateTimeOffset.UtcNow;
        ClearFailureOnceAccepted(job);

        // Accrue the durable lifetime savings tally in the same transaction as the replacement,
        // so the Dashboard headline reflects this file and survives later row/history changes.
        await _lifetime.ApplyReplacementAsync(originalSize, outputSize, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);

        // The verified output moved out of /work into the original's place; tidy the now-empty
        // per-media-file scratch directory it came from.
        if (_workRoot is not null)
        {
            WorkPaths.PruneEmptyAncestors(_workRoot, job.WorkOutputPath!);
        }

        // Refresh the inventory from the file now living at the original's location.
        // Best effort: the replacement is already committed and must not be undone
        // just because a re-probe could not run.
        await TryReprobeAsync(media.Id, cancellationToken);

        // Best effort: tell connected media servers to re-scan the new file.
        await TryRefreshLibrariesAsync(media.Path, cancellationToken);

        // Best effort: notify configured targets of the replacement.
        if (_notifications is not null)
        {
            try
            {
                await _notifications.NotifyReplacementAsync(media.Path, originalSize, outputSize, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Replacement notification for {Path} failed", media.Path);
            }
        }

        return ReplacementActionResult.Ok(replacement);
    }

    /// <summary>
    /// Reconciles interrupted replacement and rollback operations from their durably recorded
    /// intent. Ambiguous states retain the record and are logged rather than guessed destructively.
    /// </summary>
    public async Task<int> RecoverPendingAsync(CancellationToken cancellationToken)
    {
        var pending = await _db.Replacements
            .Include(replacement => replacement.Job)
            .Where(replacement => replacement.Status == ReplacementStatus.Pending
                || replacement.Status == ReplacementStatus.RollbackPending)
            .ToListAsync(cancellationToken);
        var recovered = 0;

        foreach (var replacement in pending)
        {
            if (!await AreLibraryPathsSafeAsync(replacement.MediaFileId,
                    [replacement.OriginalPath, replacement.FinalPath, RollbackStagingPath(replacement)], cancellationToken))
            {
                LogUnrecoverablePending(replacement);
                continue;
            }
            var keys = FileOperationClaims.Keys(replacement.MediaFileId, replacement.OriginalPath,
                replacement.QuarantinePath, replacement.FinalPath);
            if (!await _coordinator.TryBeginAsync(replacement.JobId, replacement.MediaFileId, cancellationToken, keys)) continue;
            try
            {
                if (await DedupeJournalStore.IsReservedAsync(_db, keys, cancellationToken)) continue;
                recovered += replacement.Status == ReplacementStatus.RollbackPending
                    ? await RecoverPendingRollbackAsync(replacement, cancellationToken) ? 1 : 0
                    : await RecoverPendingReplacementAsync(replacement, cancellationToken) ? 1 : 0;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Recovery runs before the host starts. One record it cannot commit must not stop the
                // server from starting: its files stay where they are and the record stays pending.
                DiscardUnsavedChanges();
                _logger.LogError(ex, "Recovery of pending operation {ReplacementId} could not be committed", replacement.Id);
                LogUnrecoverablePending(replacement);
            }
            finally { _coordinator.End(replacement.JobId, replacement.MediaFileId); }
        }

        return recovered;
    }

    public async Task<ReplacementActionResult> RollbackAsync(int replacementId, CancellationToken cancellationToken)
    {
        var replacement = await _db.Replacements
            .FirstOrDefaultAsync(r => r.Id == replacementId, cancellationToken);

        if (replacement is null)
        {
            return ReplacementActionResult.NotFound($"No replacement with id {replacementId}.");
        }

        var fileKeys = FileOperationClaims.Keys(replacement.MediaFileId, replacement.OriginalPath,
            replacement.QuarantinePath, replacement.FinalPath);
        if (!await _coordinator.TryBeginAsync(replacement.JobId, replacement.MediaFileId, cancellationToken, fileKeys))
        {
            return ReplacementActionResult.Invalid(
                $"A replacement or rollback for job {replacement.JobId} is already in progress.");
        }

        try
        {
            if (await DedupeJournalStore.IsReservedAsync(_db, fileKeys, cancellationToken))
                return ReplacementActionResult.Deferred("An unresolved exact-copy action reserves this source or destination. Resolve its journal before rollback.");
            return await RollbackCoreAsync(replacement, cancellationToken);
        }
        finally
        {
            _coordinator.End(replacement.JobId, replacement.MediaFileId);
        }
    }

    private async Task<ReplacementActionResult> RollbackCoreAsync(
        ReplacementEntity replacement,
        CancellationToken cancellationToken)
    {
        var replacementId = replacement.Id;

        if (replacement.Status != ReplacementStatus.Replaced)
        {
            return ReplacementActionResult.Invalid($"Replacement {replacementId} is already {replacement.Status}.");
        }

        if (!File.Exists(replacement.QuarantinePath))
        {
            return ReplacementActionResult.Failed(
                $"The quarantined original is missing, so it cannot be restored: {replacement.QuarantinePath}");
        }

        if (!await AreLibraryPathsSafeAsync(replacement.MediaFileId,
                [replacement.OriginalPath, replacement.FinalPath, RollbackStagingPath(replacement)], cancellationToken))
            return ReplacementActionResult.Deferred(LibraryPathSafety.Refusal);

        if (!string.Equals(replacement.OriginalPath, replacement.FinalPath, StringComparison.Ordinal)
            && await IsInventoryPathOwnedByAnotherRecordAsync(replacement.MediaFileId, replacement.OriginalPath, cancellationToken))
        {
            return ReplacementActionResult.Failed(
                $"Rollback would restore {replacement.OriginalPath}, which the inventory now records for another file with job or "
                + "replacement history. Both files were left where they are.");
        }

        replacement.Status = ReplacementStatus.RollbackPending;
        await _db.SaveChangesAsync(cancellationToken);

        string? stagedOutputPath = null;
        try
        {
            // Stage the optimised output instead of deleting it. If restoring the original fails,
            // put this known-good file back so the library never loses its live copy.
            if (File.Exists(replacement.FinalPath))
            {
                stagedOutputPath = RollbackStagingPath(replacement);
                _moveFile(replacement.FinalPath, stagedOutputPath);
            }

            try
            {
                _moveFile(replacement.QuarantinePath, replacement.OriginalPath);
            }
            catch
            {
                RestoreStagedOutput(stagedOutputPath, replacement.FinalPath);
                throw;
            }

        }
        catch (Exception ex)
        {
            if (File.Exists(replacement.FinalPath)
                && File.Exists(replacement.QuarantinePath)
                && RemoveFailedRollbackOriginalCopy(replacement))
            {
                replacement.Status = ReplacementStatus.Replaced;
                await _db.SaveChangesAsync(cancellationToken);
            }

            _logger.LogError(ex, "Rollback of replacement {ReplacementId} failed", replacementId);
            return ReplacementActionResult.Failed($"Rollback failed: {ex.Message}");
        }

        if (!await TryClaimInventoryPathAsync(replacement.MediaFileId, replacement.OriginalPath, cancellationToken))
        {
            LogUnrecoverablePending(replacement);
            return ReplacementActionResult.Failed(
                $"The original was restored, but the inventory now records another file at {replacement.OriginalPath}. "
                + "The rollback stays pending until that is resolved.");
        }

        var media = await _db.MediaFiles.FirstOrDefaultAsync(f => f.Id == replacement.MediaFileId, cancellationToken);
        if (media is not null)
        {
            media.Path = replacement.OriginalPath;
            media.SizeBytes = replacement.OriginalSizeBytes;
            media.UpdatedAt = DateTimeOffset.UtcNow;
        }

        replacement.Status = ReplacementStatus.RolledBack;
        replacement.RolledBackAt = DateTimeOffset.UtcNow;

        // The original is back in place, so this replacement saved nothing: reverse its
        // contribution to the lifetime tally in the same transaction.
        await _lifetime.ApplyRollbackAsync(replacement.OriginalSizeBytes, replacement.NewSizeBytes, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        TryDeleteStagedOutput(stagedOutputPath);

        if (media is not null)
        {
            await TryReprobeAsync(media.Id, cancellationToken);
        }

        // Best effort: the restored original is back in place; have servers re-scan it.
        await TryRefreshLibrariesAsync(replacement.OriginalPath, cancellationToken);

        return ReplacementActionResult.Ok(replacement);
    }

    private async Task TryRefreshLibrariesAsync(string changedPath, CancellationToken cancellationToken)
    {
        if (_refresh is null)
        {
            return;
        }

        try
        {
            await _refresh.RefreshForPathAsync(changedPath, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Library refresh after change to {Path} failed", changedPath);
        }
    }

    private async Task TryReprobeAsync(int mediaFileId, CancellationToken cancellationToken)
    {
        try
        {
            await _inventory.ProbeAsync(mediaFileId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Re-probe of media file {MediaFileId} after replacement failed", mediaFileId);
        }
    }

    private async Task<bool> AreLibraryPathsSafeAsync(int mediaFileId, IEnumerable<string> paths, CancellationToken cancellationToken)
    {
        var source = await _db.MediaFiles.AsNoTracking().Where(file => file.Id == mediaFileId)
            .Select(file => new { Root = file.Library == null ? null : file.Library.Path })
            .FirstOrDefaultAsync(cancellationToken);
        return source is not null && paths.All(path => LibraryPathSafety.IsSafeFile(path, source.Root, allowMissingLeaf: true));
    }

    private async Task<bool> RestoreFromQuarantineAsync(ReplacementPlan plan, string originalPath, string? expectedOutputHash, string? expectedOriginalHash, int mediaFileId)
    {
        if (!await AreLibraryPathsSafeAsync(mediaFileId, [originalPath, plan.FinalPath], CancellationToken.None))
            return false;
        // The original is only safe to restore if it actually reached quarantine. If the
        // quarantine move itself failed, the original is still at its own path — leave it be.
        if (!File.Exists(plan.QuarantinePath))
        {
            return File.Exists(originalPath);
        }

        try
        {
            // Preserve every file if the recorded quarantine identity is no longer trustworthy.
            // Legacy records without identities may still restore into an empty original path.
            if (FileContentIdentity.IsHash(expectedOriginalHash)
                && !await FileContentIdentity.MatchesAsync(plan.QuarantinePath, expectedOriginalHash, CancellationToken.None))
                return false;
            // Only a byte-identical verified candidate is disposable. An unrelated or partial
            // file at either path may belong to another writer; keep both it and the quarantine
            // with the pending recovery record rather than guessing which file to overwrite.
            if (File.Exists(originalPath))
            {
                if (!string.Equals(plan.FinalPath, originalPath, StringComparison.Ordinal)
                    || !await FileContentIdentity.MatchesAsync(originalPath, expectedOutputHash, CancellationToken.None))
                    return false;
                File.Delete(originalPath);
            }
            if (!string.Equals(plan.FinalPath, originalPath, StringComparison.Ordinal) && File.Exists(plan.FinalPath))
            {
                if (!await FileContentIdentity.MatchesAsync(plan.FinalPath, expectedOutputHash, CancellationToken.None))
                    return false;
                File.Delete(plan.FinalPath);
            }

            FileMover.Move(plan.QuarantinePath, originalPath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Could not restore original from quarantine {Quarantine} to {Original}; the original is preserved in quarantine",
                plan.QuarantinePath, originalPath);
            return false;
        }
    }

    private async Task<bool> RecoverPendingReplacementAsync(
        ReplacementEntity replacement,
        CancellationToken cancellationToken)
    {
        var workOutputExists = replacement.Job?.WorkOutputPath is { Length: > 0 } workOutputPath
            && File.Exists(workOutputPath);
        var quarantineExists = File.Exists(replacement.QuarantinePath);
        var finalIsComplete = FileHasLength(replacement.FinalPath, replacement.NewSizeBytes);

        if (quarantineExists && !workOutputExists && finalIsComplete
            && await FileContentIdentity.MatchesAsync(replacement.FinalPath, replacement.OutputSha256, cancellationToken)
            && await FileContentIdentity.MatchesAsync(replacement.QuarantinePath, replacement.OriginalSha256, cancellationToken))
        {
            if (await TryFinalizeRecoveredReplacementAsync(replacement, cancellationToken))
                return true;
            // The finished replacement can never be recorded, so put the original back instead.
            _logger.LogWarning(
                "Pending replacement {ReplacementId} landed on {Final}, which the inventory records for another file; restoring the original",
                replacement.Id, replacement.FinalPath);
        }

        if (quarantineExists)
        {
            var plan = new ReplacementPlan(
                replacement.OriginalPath,
                replacement.FinalPath,
                replacement.QuarantinePath);
            if (await RestoreFromQuarantineAsync(plan, replacement.OriginalPath, replacement.OutputSha256, replacement.OriginalSha256, replacement.MediaFileId))
            {
                _db.Replacements.Remove(replacement);
                ForgetUnfinishedAcceptance(replacement.Job);
                await _db.SaveChangesAsync(cancellationToken);
                _logger.LogWarning(
                    "Restored original for interrupted pending replacement {ReplacementId}",
                    replacement.Id);
                return true;
            }
        }
        else if (File.Exists(replacement.OriginalPath) && workOutputExists)
        {
            // The process stopped after recording Pending but before moving the original.
            _db.Replacements.Remove(replacement);
            ForgetUnfinishedAcceptance(replacement.Job);
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }

        LogUnrecoverablePending(replacement);
        return false;
    }

    private async Task<bool> RecoverPendingRollbackAsync(
        ReplacementEntity replacement,
        CancellationToken cancellationToken)
    {
        var stagedOutputPath = RollbackStagingPath(replacement);
        var quarantineExists = File.Exists(replacement.QuarantinePath);

        if (!quarantineExists && FileHasLength(replacement.OriginalPath, replacement.OriginalSizeBytes))
        {
            if (!await TryFinalizeRecoveredRollbackAsync(replacement, cancellationToken))
                return false;
            TryDeleteStagedOutput(stagedOutputPath);
            return true;
        }

        if (quarantineExists
            && File.Exists(replacement.FinalPath)
            && RemoveFailedRollbackOriginalCopy(replacement))
        {
            replacement.Status = ReplacementStatus.Replaced;
            await _db.SaveChangesAsync(cancellationToken);
            TryDeleteStagedOutput(stagedOutputPath);
            _logger.LogWarning(
                "Restored replacement state for interrupted rollback {ReplacementId}",
                replacement.Id);
            return true;
        }

        if (quarantineExists && File.Exists(stagedOutputPath))
        {
            try
            {
                _moveFile(stagedOutputPath, replacement.FinalPath);
                replacement.Status = ReplacementStatus.Replaced;
                await _db.SaveChangesAsync(cancellationToken);
                _logger.LogWarning(
                    "Restored staged optimised output for interrupted rollback {ReplacementId}",
                    replacement.Id);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogCritical(
                    ex,
                    "Could not restore staged output for pending rollback {ReplacementId}",
                    replacement.Id);
                return false;
            }
        }

        if (quarantineExists)
        {
            // The optimised file is no longer available. Restore the protected original rather
            // than leave the library path empty, then commit the rollback state.
            var plan = new ReplacementPlan(
                replacement.OriginalPath,
                replacement.FinalPath,
                replacement.QuarantinePath);
            if (await RestoreFromQuarantineAsync(plan, replacement.OriginalPath, replacement.OutputSha256, replacement.OriginalSha256, replacement.MediaFileId))
            {
                return await TryFinalizeRecoveredRollbackAsync(replacement, cancellationToken);
            }
        }

        LogUnrecoverablePending(replacement);
        return false;
    }

    private async Task<bool> TryFinalizeRecoveredRollbackAsync(
        ReplacementEntity replacement,
        CancellationToken cancellationToken)
    {
        if (!await TryClaimInventoryPathAsync(replacement.MediaFileId, replacement.OriginalPath, cancellationToken))
        {
            LogUnrecoverablePending(replacement);
            return false;
        }

        var media = await _db.MediaFiles
            .FirstOrDefaultAsync(file => file.Id == replacement.MediaFileId, cancellationToken);
        if (media is not null)
        {
            media.Path = replacement.OriginalPath;
            media.SizeBytes = replacement.OriginalSizeBytes;
            media.UpdatedAt = DateTimeOffset.UtcNow;
        }

        replacement.Status = ReplacementStatus.RolledBack;
        replacement.RolledBackAt = DateTimeOffset.UtcNow;
        await _lifetime.ApplyRollbackAsync(
            replacement.OriginalSizeBytes,
            replacement.NewSizeBytes,
            cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogWarning(
            "Finalized interrupted rollback {ReplacementId}; original was already restored",
            replacement.Id);
        return true;
    }

    private async Task<bool> TryFinalizeRecoveredReplacementAsync(
        ReplacementEntity replacement,
        CancellationToken cancellationToken)
    {
        if (!await TryClaimInventoryPathAsync(replacement.MediaFileId, replacement.FinalPath, cancellationToken))
            return false;

        var media = await _db.MediaFiles
            .FirstOrDefaultAsync(file => file.Id == replacement.MediaFileId, cancellationToken);
        if (media is not null)
        {
            media.Path = replacement.FinalPath;
            media.SizeBytes = replacement.NewSizeBytes;
            media.UpdatedAt = DateTimeOffset.UtcNow;
        }

        if (replacement.Job is not null)
        {
            replacement.Job.Status = JobStatus.Completed;
            replacement.Job.UpdatedAt = DateTimeOffset.UtcNow;
            ClearFailureOnceAccepted(replacement.Job);
        }

        replacement.Status = ReplacementStatus.Replaced;
        await _lifetime.ApplyReplacementAsync(
            replacement.OriginalSizeBytes,
            replacement.NewSizeBytes,
            cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogWarning(
            "Finalized interrupted replacement {ReplacementId}; verified output was already in place",
            replacement.Id);
        return true;
    }

    private void RestoreStagedOutput(string? stagedOutputPath, string finalPath)
    {
        if (stagedOutputPath is null || !File.Exists(stagedOutputPath) || File.Exists(finalPath))
        {
            return;
        }

        try
        {
            _moveFile(stagedOutputPath, finalPath);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(
                ex,
                "Could not restore staged optimised output {StagedOutput} to {FinalPath}",
                stagedOutputPath,
                finalPath);
        }
    }

    private void TryDeleteStagedOutput(string? stagedOutputPath)
    {
        if (stagedOutputPath is null)
        {
            return;
        }

        try
        {
            File.Delete(stagedOutputPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not remove staged rollback output {Path}", stagedOutputPath);
        }
    }

    private static string RollbackStagingPath(ReplacementEntity replacement) =>
        replacement.FinalPath + $".optimisarr-rollback-{replacement.Id}.tmp";

    private bool RemoveFailedRollbackOriginalCopy(ReplacementEntity replacement)
    {
        if (string.Equals(replacement.OriginalPath, replacement.FinalPath, StringComparison.Ordinal)
            || !File.Exists(replacement.OriginalPath))
        {
            return true;
        }

        try
        {
            // A failed cross-filesystem restore can leave a verified copy at OriginalPath while the
            // protected source still exists in quarantine. Prove the two files are identical before
            // removing the duplicate; an unrelated file that appeared at the path is never touched.
            FileMover.VerifyCopiedContent(replacement.QuarantinePath, replacement.OriginalPath);
            File.Delete(replacement.OriginalPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogCritical(
                ex,
                "Could not remove partial rollback copy {OriginalPath} for replacement {ReplacementId}",
                replacement.OriginalPath,
                replacement.Id);
            return false;
        }
    }

    // A row with work or replacement history describes another file's record, which is never removed.
    private Task<bool> IsInventoryPathOwnedByAnotherRecordAsync(int mediaFileId, string path, CancellationToken cancellationToken) =>
        _db.MediaFiles.AsNoTracking().AnyAsync(file => file.Path == path && file.Id != mediaFileId
            && (_db.Jobs.Any(job => job.MediaFileId == file.Id)
                || _db.Replacements.Any(replacement => replacement.MediaFileId == file.Id)
                || _db.DedupeActionFiles.Any(dedupe => dedupe.MediaFileId == file.Id)), cancellationToken);

    /// <summary>
    /// Makes <paramref name="path"/> free for this record's row to take. A scan or an import notification can
    /// record the file this operation just placed as a new row before the operation commits; that row has no
    /// history and describes the same file, so it is folded away in the same save. False when a row with
    /// history holds the path, which this operation must not take from it.
    /// </summary>
    private async Task<bool> TryClaimInventoryPathAsync(int mediaFileId, string path, CancellationToken cancellationToken)
    {
        if (await IsInventoryPathOwnedByAnotherRecordAsync(mediaFileId, path, cancellationToken))
            return false;
        var duplicate = await _db.MediaFiles.FirstOrDefaultAsync(file => file.Path == path && file.Id != mediaFileId, cancellationToken);
        if (duplicate is not null)
            _db.MediaFiles.Remove(duplicate);
        return true;
    }

    private void DiscardUnsavedChanges()
    {
        foreach (var entry in _db.ChangeTracker.Entries().ToList())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.State = EntityState.Detached;
                    break;
                case EntityState.Modified or EntityState.Deleted:
                    entry.CurrentValues.SetValues(entry.OriginalValues);
                    entry.State = EntityState.Unchanged;
                    break;
            }
        }
    }

    private void LogUnrecoverablePending(ReplacementEntity replacement)
    {
        _logger.LogCritical(
            "Pending {Status} operation {ReplacementId} could not be recovered automatically. Original={Original}; Quarantine={Quarantine}; Final={Final}",
            replacement.Status,
            replacement.Id,
            replacement.OriginalPath,
            replacement.QuarantinePath,
            replacement.FinalPath);
    }

    private static bool FileHasLength(string path, long expectedLength)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists && file.Length == expectedLength;
        }
        catch (IOException)
        {
            return false;
        }
    }

}
