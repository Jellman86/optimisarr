using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Queue;
using Optimisarr.Core.Calibration;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Rules;
using Optimisarr.Data;

namespace Optimisarr.Api.Library;

public sealed record CalibrationSourceDto(
    int MediaFileId,
    string RelativePath,
    double DurationSeconds,
    int? Width,
    int? Height);

public sealed record CalibrationSlotDto(string Name, string Url, double StartSeconds);

public sealed record CalibrationTrialDto(
    Guid Id,
    string Phase,
    int Number,
    int SampleNumber,
    int SampleCount,
    int DurationSeconds,
    CalibrationSlotDto A,
    CalibrationSlotDto B,
    CalibrationSlotDto X);

public sealed record CalibrationResultDto(
    int? RecommendedQuality,
    string? Encoder,
    string? QualityMode,
    int? EffectiveQuality,
    double? EstimatedSavingPercent,
    int CorrectAnswers,
    int TotalAnswers,
    string Outcome,
    bool Applied);

public sealed record CalibrationSessionDto(
    Guid Id,
    int LibraryId,
    int MediaFileId,
    string Source,
    string Status,
    double PreparationProgress,
    string? Error,
    CalibrationTrialDto? Trial,
    CalibrationResultDto? Result);

public sealed record CalibrationStream(string Path);

internal interface ICalibrationRandomizer
{
    bool NextBit();
}

internal sealed class CryptographicCalibrationRandomizer : ICalibrationRandomizer
{
    public bool NextBit() => RandomNumberGenerator.GetInt32(2) == 1;
}

/// <summary>
/// Owns short-lived blind calibration sessions. Candidate media is represented by disposable queue
/// jobs under /work/calibration; only the final explicit Apply operation can change a library row.
/// </summary>
internal sealed class BlindCalibrationService(
    IServiceScopeFactory scopeFactory,
    QueueDispatcher dispatcher,
    IHostEnvironment environment,
    TimeProvider timeProvider,
    ICalibrationRandomizer randomizer) : BackgroundService
{
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(2);
    private readonly ConcurrentDictionary<Guid, Session> _sessions = new();
    private readonly string _workRoot = WorkPaths.Resolve(environment);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5), timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RemoveExpiredAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
    }

    public async Task<IReadOnlyList<CalibrationSourceDto>> ListSourcesAsync(
        int libraryId,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        return await db.MediaFiles
            .AsNoTracking()
            .Where(file => file.LibraryId == libraryId
                && file.Status == MediaFileStatus.Probed
                && file.MediaKind == MediaKind.Video
                && !file.IsHdr
                && !file.IsDolbyVision
                && file.DurationSeconds >= BlindCalibrationPolicy.SampleSeconds * 4)
            .OrderByDescending(file => file.SizeBytes)
            .Take(25)
            .Select(file => new CalibrationSourceDto(
                file.Id,
                file.RelativePath,
                file.DurationSeconds!.Value,
                file.Width,
                file.Height))
            .ToListAsync(cancellationToken);
    }

    public async Task<CalibrationSessionDto> CreateAsync(
        int libraryId,
        int mediaFileId,
        CancellationToken cancellationToken)
    {
        await RemoveExpiredAsync(cancellationToken);
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var media = await db.MediaFiles
            .AsNoTracking()
            .Include(file => file.Library)
            .FirstOrDefaultAsync(
                file => file.Id == mediaFileId && file.LibraryId == libraryId,
                cancellationToken);
        if (media?.Library is null)
        {
            throw new KeyNotFoundException("The calibration source or library no longer exists.");
        }
        if (media.MediaKind != MediaKind.Video || media.IsHdr || media.IsDolbyVision)
        {
            throw new InvalidOperationException("Blind calibration currently supports SDR video only.");
        }
        if (media.DurationSeconds is not { } duration)
        {
            throw new InvalidOperationException("Probe the source before starting calibration.");
        }

        var rules = LibraryRuleResolution.Resolve(media.Library);
        if (rules.TargetVideoCodec is null || rules.DefaultCrf is null)
        {
            throw new InvalidOperationException("Choose a video re-encode preset before calibrating quality.");
        }

        var currentQuality = media.Library.QualityCrf ?? rules.DefaultCrf.Value;
        var plan = BlindCalibrationPolicy.Plan(duration, currentQuality);
        var id = Guid.NewGuid();
        var fingerprint = Fingerprint.From(media.Library, rules, currentQuality);
        var candidates = new List<Candidate>();

        foreach (var quality in plan.RequestedQualities)
        {
            foreach (var sample in plan.Samples)
            {
                var job = new Job
                {
                    MediaFileId = media.Id,
                    // Interactive work must not be held by a library's automatic overnight window.
                    LibraryId = null,
                    Type = JobType.Calibration,
                    Status = JobStatus.Queued,
                    Priority = int.MaxValue,
                    RequestedVideoQuality = quality,
                    CalibrationSessionId = id,
                    CalibrationClipStartSeconds = sample.StartSeconds,
                    CalibrationClipSeconds = sample.DurationSeconds,
                    EnqueuedAt = timeProvider.GetUtcNow()
                };
                db.Jobs.Add(job);
                candidates.Add(new Candidate(job, quality, sample));
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        var session = new Session(
            id,
            libraryId,
            media.Id,
            media.RelativePath,
            media.SizeBytes,
            duration,
            plan,
            fingerprint,
            candidates,
            timeProvider.GetUtcNow().Add(SessionLifetime));
        if (!_sessions.TryAdd(id, session))
        {
            throw new InvalidOperationException("Could not create a unique calibration session.");
        }

        dispatcher.Wake();
        return await GetAsync(id, cancellationToken)
            ?? throw new InvalidOperationException("Calibration session was not retained.");
    }

    public async Task<CalibrationSessionDto?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await RemoveExpiredAsync(cancellationToken);
        if (!_sessions.TryGetValue(id, out var session))
        {
            return null;
        }
        lock (session.Gate)
        {
            // Active polling and trials keep the session alive; abandoned panels are reaped two
            // hours after their last request, even if the container itself keeps running.
            session.ExpiresAt = timeProvider.GetUtcNow().Add(SessionLifetime);
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var jobs = await db.Jobs
            .AsNoTracking()
            .Where(job => job.CalibrationSessionId == id)
            .ToListAsync(cancellationToken);

        lock (session.Gate)
        {
            if (session.Status == SessionStatus.Preparing)
            {
                var failed = jobs.FirstOrDefault(job => job.Status is JobStatus.Failed or JobStatus.Cancelled);
                if (failed is not null)
                {
                    session.Status = SessionStatus.Failed;
                    session.Error = failed.ErrorMessage ?? "A calibration candidate could not be prepared.";
                }
                else if (jobs.Count == session.Candidates.Count
                    && jobs.All(job => job.Status == JobStatus.Completed))
                {
                    session.Status = SessionStatus.Screening;
                    session.CurrentTrial = CreateTrial(session);
                }
            }

            return ToDto(session, jobs);
        }
    }

    public async Task<CalibrationSessionDto> AnswerAsync(
        Guid id,
        Guid trialId,
        string choice,
        CancellationToken cancellationToken)
    {
        _ = await GetAsync(id, cancellationToken)
            ?? throw new KeyNotFoundException("Calibration session was not found.");
        if (!_sessions.TryGetValue(id, out var session))
        {
            throw new KeyNotFoundException("Calibration session was not found.");
        }

        lock (session.Gate)
        {
            var trial = session.CurrentTrial;
            if (trial is null || trial.Id != trialId)
            {
                throw new InvalidOperationException("This trial has already been answered or is no longer current.");
            }
            if (choice is not ("A" or "B"))
            {
                throw new InvalidOperationException("Choose whether X matches A or B.");
            }

            var correct = string.Equals(choice, trial.CorrectChoice, StringComparison.Ordinal);
            session.StageAnswers.Add(correct);
            session.AllAnswers.Add(correct);
            session.CurrentTrial = null;

            var judgement = session.Status == SessionStatus.Screening
                ? BlindCalibrationPolicy.JudgeScreening(
                    session.StageAnswers.Count(answer => answer),
                    session.StageAnswers.Count)
                : BlindCalibrationPolicy.JudgeConfirmation(
                    session.StageAnswers.Count(answer => answer),
                    session.StageAnswers.Count);
            Advance(session, judgement);
            if (session.Status is SessionStatus.Screening or SessionStatus.Confirming)
            {
                session.CurrentTrial = CreateTrial(session);
            }
        }

        return await GetAsync(id, cancellationToken)
            ?? throw new KeyNotFoundException("Calibration session was not found.");
    }

    public async Task<CalibrationSessionDto> RevealAsync(Guid id, CancellationToken cancellationToken)
    {
        _ = await GetAsync(id, cancellationToken)
            ?? throw new KeyNotFoundException("Calibration session was not found.");
        if (!_sessions.TryGetValue(id, out var session))
        {
            throw new KeyNotFoundException("Calibration session was not found.");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var jobs = await db.Jobs
            .AsNoTracking()
            .Where(job => job.CalibrationSessionId == id)
            .ToListAsync(cancellationToken);

        lock (session.Gate)
        {
            if (session.Status is not (SessionStatus.Complete or SessionStatus.Revealed or SessionStatus.Applied))
            {
                throw new InvalidOperationException("Finish the blind trials before revealing the settings.");
            }
            if (session.Status == SessionStatus.Complete)
            {
                session.Status = SessionStatus.Revealed;
            }
            session.Result ??= BuildResult(session, jobs);
            return ToDto(session, jobs);
        }
    }

    public async Task<CalibrationSessionDto> ApplyAsync(Guid id, CancellationToken cancellationToken)
    {
        _ = await RevealAsync(id, cancellationToken);
        if (!_sessions.TryGetValue(id, out var session))
        {
            throw new KeyNotFoundException("Calibration session was not found.");
        }
        if (session.RecommendedQuality is null)
        {
            throw new InvalidOperationException("This calibration did not find a quality setting to apply.");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var library = await db.Libraries.FirstOrDefaultAsync(
            candidate => candidate.Id == session.LibraryId,
            cancellationToken)
            ?? throw new KeyNotFoundException("The calibrated library no longer exists.");
        var rules = LibraryRuleResolution.Resolve(library);
        var currentQuality = library.QualityCrf ?? rules.DefaultCrf;
        if (currentQuality is null || !session.Fingerprint.Matches(library, rules, currentQuality.Value))
        {
            throw new InvalidOperationException(
                "The library's codec, preset, or quality changed during calibration. Start a new calibration.");
        }

        library.QualityCrf = session.RecommendedQuality;
        await db.SaveChangesAsync(cancellationToken);
        lock (session.Gate)
        {
            session.Status = SessionStatus.Applied;
            session.Result = session.Result! with { Applied = true };
        }
        return await GetAsync(id, cancellationToken)
            ?? throw new KeyNotFoundException("Calibration session was not found.");
    }

    public async Task<CalibrationStream?> ResolveStreamAsync(
        Guid sessionId,
        Guid trialId,
        string slot,
        CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            return null;
        }

        Trial trial;
        lock (session.Gate)
        {
            if (session.CurrentTrial is not { } current || current.Id != trialId)
            {
                return null;
            }
            trial = current;
        }

        var source = slot switch
        {
            "A" => trial.A,
            "B" => trial.B,
            "X" => trial.X,
            _ => null
        };
        if (source is null)
        {
            return null;
        }
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var outputPath = await db.Jobs
            .AsNoTracking()
            .Where(job => job.Id == source.JobId
                && job.CalibrationSessionId == sessionId
                && job.Status == JobStatus.Completed)
            .Select(job => job.WorkOutputPath)
            .FirstOrDefaultAsync(cancellationToken);
        if (outputPath is null)
        {
            return null;
        }

        // Serve the verifier's stream-copy reference clip, not the full original. This keeps the
        // native controls on the same short 0-based timeline as the encoded candidate, avoiding a
        // duration/seek-position side channel that would reveal which blind slot is the original.
        var path = source.Original
            ? Path.Combine(Path.GetDirectoryName(outputPath)!, ".optimisarr-comparison-reference.mkv")
            : outputPath;
        return new CalibrationStream(path);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!_sessions.TryRemove(id, out _))
        {
            return false;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var jobs = await db.Jobs
            .Where(job => job.CalibrationSessionId == id)
            .ToListAsync(cancellationToken);
        foreach (var job in jobs)
        {
            dispatcher.RequestCancel(job.Id);
        }
        db.Jobs.RemoveRange(jobs);
        await db.SaveChangesAsync(cancellationToken);

        var root = Path.Combine(_workRoot, "calibration", id.ToString("N"));
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A cancelling ffmpeg may still hold a file; startup cleanup removes the subtree.
        }
        return true;
    }

    private async Task RemoveExpiredAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var expired = _sessions.Values
            .Where(session =>
            {
                lock (session.Gate)
                {
                    return session.ExpiresAt <= now;
                }
            })
            .Select(session => session.Id)
            .ToList();
        foreach (var id in expired)
        {
            await DeleteAsync(id, cancellationToken);
        }
    }

    private static void Advance(Session session, CalibrationJudgement judgement)
    {
        if (judgement == CalibrationJudgement.Continue)
        {
            return;
        }
        if (session.Status == SessionStatus.Screening
            && judgement == CalibrationJudgement.NoReliableDifference)
        {
            session.Status = SessionStatus.Confirming;
            session.StageAnswers.Clear();
            return;
        }
        if (session.Status == SessionStatus.Confirming
            && judgement == CalibrationJudgement.NoReliableDifference)
        {
            session.RecommendedQuality = session.Plan.RequestedQualities[session.QualityIndex];
            session.Status = SessionStatus.Complete;
            return;
        }

        session.QualityIndex++;
        session.StageAnswers.Clear();
        if (session.QualityIndex >= session.Plan.RequestedQualities.Count)
        {
            session.Status = SessionStatus.Complete;
            return;
        }
        session.Status = SessionStatus.Screening;
    }

    private Trial CreateTrial(Session session)
    {
        var sample = session.Plan.Samples[session.StageAnswers.Count % session.Plan.Samples.Count];
        var quality = session.Plan.RequestedQualities[session.QualityIndex];
        var candidate = session.Candidates.Single(item =>
            item.Quality == quality && item.Sample.Index == sample.Index);
        var aIsOriginal = randomizer.NextBit();
        var xMatchesA = randomizer.NextBit();
        var a = new TrialSource(aIsOriginal, candidate.Job.Id);
        var b = new TrialSource(!aIsOriginal, candidate.Job.Id);
        return new Trial(
            Guid.NewGuid(),
            a,
            b,
            xMatchesA ? a : b,
            xMatchesA ? "A" : "B",
            sample,
            session.AllAnswers.Count + 1);
    }

    private static CalibrationResultDto BuildResult(Session session, IReadOnlyList<Job> jobs)
    {
        if (session.RecommendedQuality is not { } quality)
        {
            return new CalibrationResultDto(
                null,
                null,
                null,
                null,
                null,
                session.AllAnswers.Count(answer => answer),
                session.AllAnswers.Count,
                "NoTransparentSetting",
                false);
        }

        var candidateJobs = jobs
            .Where(job => job.RequestedVideoQuality == quality && job.OutputSizeBytes is > 0)
            .ToList();
        var encodedBytesPerSecond = candidateJobs.Count == 0
            ? (double?)null
            : candidateJobs.Average(job => job.OutputSizeBytes!.Value / (double)BlindCalibrationPolicy.SampleSeconds);
        var sourceBytesPerSecond = session.SourceSizeBytes / session.SourceDurationSeconds;
        double? saving = encodedBytesPerSecond is null || sourceBytesPerSecond <= 0
            ? null
            : Math.Round((1 - encodedBytesPerSecond.Value / sourceBytesPerSecond) * 100, 1);
        var representative = candidateJobs.FirstOrDefault();
        return new CalibrationResultDto(
            quality,
            representative?.VideoEncoder,
            representative?.VideoQualityMode,
            representative?.EffectiveVideoQuality,
            saving,
            session.AllAnswers.Count(answer => answer),
            session.AllAnswers.Count,
            "NoReliableDifference",
            false);
    }

    private static CalibrationSessionDto ToDto(Session session, IReadOnlyList<Job> jobs)
    {
        var progress = jobs.Count == 0
            ? 0
            : jobs.Average(job => job.Status == JobStatus.Completed ? 1 : Math.Clamp(job.Progress, 0, 1));
        return new CalibrationSessionDto(
            session.Id,
            session.LibraryId,
            session.MediaFileId,
            session.SourceName,
            session.Status.ToString(),
            Math.Round(progress, 3),
            session.Error,
            session.CurrentTrial is { } trial ? ToTrialDto(session, trial) : null,
            session.Status is SessionStatus.Revealed or SessionStatus.Applied ? session.Result : null);
    }

    private static CalibrationTrialDto ToTrialDto(Session session, Trial trial)
    {
        CalibrationSlotDto Slot(string name, TrialSource source) => new(
            name,
            $"/api/calibration/{session.Id}/trials/{trial.Id}/content/{name}",
            0);
        return new CalibrationTrialDto(
            trial.Id,
            session.Status == SessionStatus.Screening ? "Screening" : "Confirmation",
            trial.Number,
            trial.Sample.Index + 1,
            session.Plan.Samples.Count,
            trial.Sample.DurationSeconds,
            Slot("A", trial.A),
            Slot("B", trial.B),
            Slot("X", trial.X));
    }

    private enum SessionStatus
    {
        Preparing,
        Screening,
        Confirming,
        Complete,
        Revealed,
        Applied,
        Failed
    }

    private sealed class Session(
        Guid id,
        int libraryId,
        int mediaFileId,
        string sourceName,
        long sourceSizeBytes,
        double sourceDurationSeconds,
        BlindCalibrationPlan plan,
        Fingerprint fingerprint,
        List<Candidate> candidates,
        DateTimeOffset expiresAt)
    {
        public object Gate { get; } = new();
        public Guid Id { get; } = id;
        public int LibraryId { get; } = libraryId;
        public int MediaFileId { get; } = mediaFileId;
        public string SourceName { get; } = sourceName;
        public long SourceSizeBytes { get; } = sourceSizeBytes;
        public double SourceDurationSeconds { get; } = sourceDurationSeconds;
        public BlindCalibrationPlan Plan { get; } = plan;
        public Fingerprint Fingerprint { get; } = fingerprint;
        public List<Candidate> Candidates { get; } = candidates;
        public DateTimeOffset ExpiresAt { get; set; } = expiresAt;
        public SessionStatus Status { get; set; } = SessionStatus.Preparing;
        public string? Error { get; set; }
        public int QualityIndex { get; set; }
        public int? RecommendedQuality { get; set; }
        public List<bool> StageAnswers { get; } = [];
        public List<bool> AllAnswers { get; } = [];
        public Trial? CurrentTrial { get; set; }
        public CalibrationResultDto? Result { get; set; }
    }

    private sealed record Candidate(Job Job, int Quality, CalibrationSample Sample);
    private sealed record TrialSource(bool Original, int JobId);
    private sealed record Trial(
        Guid Id,
        TrialSource A,
        TrialSource B,
        TrialSource X,
        string CorrectChoice,
        CalibrationSample Sample,
        int Number);

    private sealed record Fingerprint(
        RuleProfile Profile,
        string? TargetVideoCodec,
        string? TargetContainer,
        string? EncoderPreset,
        int InitialQuality)
    {
        public static Fingerprint From(Optimisarr.Data.Library library, RuleSettings rules, int quality) => new(
            library.RuleProfile,
            rules.TargetVideoCodec,
            rules.TargetContainer,
            library.EncoderPreset,
            quality);

        public bool Matches(Optimisarr.Data.Library library, RuleSettings rules, int quality) =>
            Profile == library.RuleProfile
            && string.Equals(TargetVideoCodec, rules.TargetVideoCodec, StringComparison.OrdinalIgnoreCase)
            && string.Equals(TargetContainer, rules.TargetContainer, StringComparison.OrdinalIgnoreCase)
            && string.Equals(EncoderPreset, library.EncoderPreset, StringComparison.OrdinalIgnoreCase)
            && InitialQuality == quality;
    }
}
