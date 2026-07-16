using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Queue;
using Optimisarr.Core.Calibration;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Rules;
using Optimisarr.Core.Verification;
using Optimisarr.Data;

namespace Optimisarr.Api.Library;

public sealed record CalibrationSourceDto(
    int MediaFileId,
    string RelativePath,
    double DurationSeconds,
    int? Width,
    int? Height,
    string MediaKind,
    bool IsHdr);

public sealed record CalibrationSlotDto(string Name, string Url, double StartSeconds, double GainDb);

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
    string MediaKind,
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
    ICalibrationRandomizer randomizer,
    LoudnessService loudness) : BackgroundService
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
                && (file.MediaKind == MediaKind.Video
                    && !file.IsDolbyVision
                    && file.DurationSeconds >= BlindCalibrationPolicy.SampleSeconds * 4
                    || file.MediaKind == MediaKind.Audio
                    && file.DurationSeconds >= BlindCalibrationPolicy.AudioSampleSeconds * 4
                    || file.MediaKind == MediaKind.Image
                    && (file.FrameCount == null || file.FrameCount <= 1)))
            .OrderByDescending(file => file.SizeBytes)
            .Take(25)
            .Select(file => new CalibrationSourceDto(
                file.Id,
                file.RelativePath,
                file.DurationSeconds ?? 0,
                file.Width,
                file.Height,
                file.MediaKind.ToString(),
                file.IsHdr))
            .ToListAsync(cancellationToken);
    }

    public async Task<CalibrationSessionDto> CreateAsync(
        int libraryId,
        int mediaFileId,
        bool hdrPlaybackConfirmed,
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
        if (media.Status != MediaFileStatus.Probed
            || media.MediaKind is not (MediaKind.Video or MediaKind.Audio or MediaKind.Image))
        {
            throw new InvalidOperationException("Choose a probed video, audio, or still-image source for this calibration.");
        }
        if (media.MediaKind != MediaKind.Image && media.DurationSeconds is null)
        {
            throw new InvalidOperationException("Probe the source before starting calibration.");
        }
        var duration = media.DurationSeconds ?? 0;

        var rules = LibraryRuleResolution.Resolve(media.Library);
        if (media.MediaKind == MediaKind.Video
            && !BlindCalibrationPolicy.CanCalibrateVideo(
                media.IsHdr, media.IsDolbyVision, rules.Hdr, hdrPlaybackConfirmed))
        {
            throw new InvalidOperationException(media.IsDolbyVision
                ? "Dolby Vision calibration is unavailable because a re-encode cannot safely preserve its dynamic metadata."
                : "HDR calibration requires Preserve HDR handling and confirmed HDR playback support.");
        }
        if (media.MediaKind == MediaKind.Video
            && (rules.TargetVideoCodec is null || rules.DefaultCrf is null))
        {
            throw new InvalidOperationException("Choose a video re-encode preset before calibrating quality.");
        }

        var currentQuality = media.MediaKind == MediaKind.Audio
            ? media.Library.AudioBitrateKbps ?? rules.AudioBitrateKbps
            : media.MediaKind == MediaKind.Image
                ? media.Library.ImageQuality ?? rules.ImageQuality
                : media.Library.QualityCrf ?? rules.DefaultCrf!.Value;
        var plan = media.MediaKind switch
        {
            MediaKind.Audio => BlindCalibrationPolicy.AudioPlan(duration, rules.TargetAudioCodec),
            MediaKind.Image => BlindCalibrationPolicy.ImagePlan(),
            _ => BlindCalibrationPolicy.Plan(duration, currentQuality)
        };
        var id = Guid.NewGuid();
        var fingerprint = Fingerprint.From(media.Library, rules, media.MediaKind, currentQuality);
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
                    RequestedVideoQuality = media.MediaKind == MediaKind.Video ? quality : null,
                    RequestedAudioBitrateKbps = media.MediaKind == MediaKind.Audio ? quality : null,
                    RequestedImageQuality = media.MediaKind == MediaKind.Image ? quality : null,
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
            media.MediaKind,
            media.MediaKind switch
            {
                MediaKind.Audio => rules.TargetAudioCodec,
                MediaKind.Image => rules.TargetImageFormat,
                _ => rules.TargetVideoCodec
            },
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

        if (session.MediaKind == MediaKind.Audio
            && jobs.Count == session.Candidates.Count
            && jobs.All(job => job.Status == JobStatus.Completed))
        {
            await PrepareAudioLevelsAsync(session, jobs, cancellationToken);
        }

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
                    if (session.MediaKind == MediaKind.Audio && !session.AudioLevelsReady)
                    {
                        return ToDto(session, jobs);
                    }
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
        var currentQuality = session.MediaKind == MediaKind.Audio
            ? library.AudioBitrateKbps ?? rules.AudioBitrateKbps
            : session.MediaKind == MediaKind.Image
                ? library.ImageQuality ?? rules.ImageQuality
                : library.QualityCrf ?? rules.DefaultCrf;
        if (currentQuality is null
            || !session.Fingerprint.Matches(library, rules, session.MediaKind, currentQuality.Value))
        {
            throw new InvalidOperationException(
                "The library's codec, preset, or quality changed during calibration. Start a new calibration.");
        }

        if (session.MediaKind == MediaKind.Audio)
        {
            library.AudioBitrateKbps = session.RecommendedQuality;
        }
        else if (session.MediaKind == MediaKind.Image)
        {
            library.ImageQuality = session.RecommendedQuality;
        }
        else
        {
            library.QualityCrf = session.RecommendedQuality;
        }
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

        // Serve the verifier's short stream-copy reference, not the full original. The trial DTO
        // supplies its hidden keyframe pre-roll offset and the shared player exposes only the common
        // sample timeline, so neither source position nor raw container duration reveals this slot.
        var path = source.Original
            ? Path.Combine(
                Path.GetDirectoryName(outputPath)!,
                session.MediaKind switch
                {
                    MediaKind.Audio => ".optimisarr-comparison-reference.flac",
                    MediaKind.Image => ".optimisarr-comparison-reference.png",
                    _ => ".optimisarr-comparison-reference.mkv"
                })
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

    private async Task PrepareAudioLevelsAsync(
        Session session,
        IReadOnlyList<Job> jobs,
        CancellationToken cancellationToken)
    {
        await session.AudioLevelGate.WaitAsync(cancellationToken);
        try
        {
            if (session.AudioLevelsReady || session.Status == SessionStatus.Failed)
            {
                return;
            }

            foreach (var job in jobs)
            {
                if (job.WorkOutputPath is null)
                {
                    throw new InvalidOperationException("An audio calibration candidate has no output path.");
                }

                var referencePath = Path.Combine(
                    Path.GetDirectoryName(job.WorkOutputPath)!,
                    ".optimisarr-comparison-reference.flac");
                var original = await loudness.MeasureAsync(referencePath, cancellationToken);
                var candidate = await loudness.MeasureAsync(job.WorkOutputPath, cancellationToken);
                if (!original.Measured || original.IntegratedLufs is null
                    || !candidate.Measured || candidate.IntegratedLufs is null)
                {
                    throw new InvalidOperationException(
                        "The audio samples could not be level-matched, so the blind comparison was stopped.");
                }

                session.AudioLevels[job.Id] = BlindCalibrationPolicy.MatchAudioLevels(
                    original.IntegratedLufs.Value,
                    candidate.IntegratedLufs.Value);
            }
            session.AudioLevelsReady = true;
        }
        catch (InvalidOperationException exception)
        {
            lock (session.Gate)
            {
                session.Status = SessionStatus.Failed;
                session.Error = exception.Message;
            }
        }
        finally
        {
            session.AudioLevelGate.Release();
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
        var levels = session.MediaKind == MediaKind.Audio
            ? session.AudioLevels[candidate.Job.Id]
            : new AudioLevelMatch(0, 0);
        var aIsOriginal = randomizer.NextBit();
        var xMatchesA = randomizer.NextBit();
        var a = new TrialSource(
            aIsOriginal,
            candidate.Job.Id,
            aIsOriginal ? levels.OriginalGainDb : levels.CandidateGainDb);
        var b = new TrialSource(
            !aIsOriginal,
            candidate.Job.Id,
            aIsOriginal ? levels.CandidateGainDb : levels.OriginalGainDb);
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
            .Where(job => (session.MediaKind switch
                {
                    MediaKind.Audio => job.RequestedAudioBitrateKbps,
                    MediaKind.Image => job.RequestedImageQuality,
                    _ => job.RequestedVideoQuality
                }) == quality
                && job.OutputSizeBytes is > 0)
            .ToList();
        var encodedMeasure = candidateJobs.Count == 0
            ? (double?)null
            : session.MediaKind == MediaKind.Image
                ? candidateJobs.Average(job => (double)job.OutputSizeBytes!.Value)
                : candidateJobs.Average(job =>
                    job.OutputSizeBytes!.Value / (double)(job.CalibrationClipSeconds ?? 1));
        var sourceMeasure = session.MediaKind == MediaKind.Image
            ? session.SourceSizeBytes
            : session.SourceSizeBytes / session.SourceDurationSeconds;
        double? saving = encodedMeasure is null || sourceMeasure <= 0
            ? null
            : Math.Round((1 - encodedMeasure.Value / sourceMeasure) * 100, 1);
        var representative = candidateJobs.FirstOrDefault();
        return new CalibrationResultDto(
            quality,
            session.MediaKind is MediaKind.Audio or MediaKind.Image
                ? session.TargetCodec
                : representative?.VideoEncoder,
            session.MediaKind switch
            {
                MediaKind.Audio => "kbps",
                MediaKind.Image => "quality",
                _ => representative?.VideoQualityMode
            },
            session.MediaKind is MediaKind.Audio or MediaKind.Image
                ? quality
                : representative?.EffectiveVideoQuality,
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
            session.MediaKind.ToString(),
            session.Status.ToString(),
            Math.Round(progress, 3),
            session.Error,
            session.CurrentTrial is { } trial ? ToTrialDto(session, trial, jobs) : null,
            session.Status is SessionStatus.Revealed or SessionStatus.Applied ? session.Result : null);
    }

    private static CalibrationTrialDto ToTrialDto(
        Session session,
        Trial trial,
        IReadOnlyList<Job> jobs)
    {
        var referenceOffsets = jobs.ToDictionary(
            job => job.Id,
            job => job.CalibrationReferenceStartSeconds ?? 0);
        CalibrationSlotDto Slot(string name, TrialSource source) => new(
            name,
            $"/api/calibration/{session.Id}/trials/{trial.Id}/content/{name}",
            source.Original && referenceOffsets.TryGetValue(source.JobId, out var offset)
                ? offset
                : 0,
            source.GainDb);
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
        MediaKind mediaKind,
        string? targetCodec,
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
        public MediaKind MediaKind { get; } = mediaKind;
        public string? TargetCodec { get; } = targetCodec;
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
        public SemaphoreSlim AudioLevelGate { get; } = new(1, 1);
        public Dictionary<int, AudioLevelMatch> AudioLevels { get; } = [];
        public bool AudioLevelsReady { get; set; }
    }

    private sealed record Candidate(Job Job, int Quality, CalibrationSample Sample);
    private sealed record TrialSource(bool Original, int JobId, double GainDb);
    private sealed record Trial(
        Guid Id,
        TrialSource A,
        TrialSource B,
        TrialSource X,
        string CorrectChoice,
        CalibrationSample Sample,
        int Number);

    private sealed record Fingerprint(
        MediaKind Kind,
        RuleProfile Profile,
        string? TargetCodec,
        string? TargetContainer,
        string? EncoderPreset,
        HdrHandling HdrHandling,
        bool DownmixToStereo,
        ImageDownscaleMode ImageDownscaleMode,
        int ImageDownscaleValue,
        int InitialQuality)
    {
        public static Fingerprint From(
            Optimisarr.Data.Library library,
            RuleSettings rules,
            MediaKind kind,
            int quality) => new(
            kind,
            library.RuleProfile,
            kind switch
            {
                MediaKind.Audio => rules.TargetAudioCodec,
                MediaKind.Image => rules.TargetImageFormat,
                _ => rules.TargetVideoCodec
            },
            rules.TargetContainer,
            kind == MediaKind.Audio ? null : library.EncoderPreset,
            rules.Hdr,
            rules.DownmixToStereo,
            rules.ImageDownscaleMode,
            rules.ImageDownscaleValue,
            quality);

        public bool Matches(
            Optimisarr.Data.Library library,
            RuleSettings rules,
            MediaKind kind,
            int quality) =>
            Kind == kind
            && Profile == library.RuleProfile
            && string.Equals(
                TargetCodec,
                kind switch
                {
                    MediaKind.Audio => rules.TargetAudioCodec,
                    MediaKind.Image => rules.TargetImageFormat,
                    _ => rules.TargetVideoCodec
                },
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(TargetContainer, rules.TargetContainer, StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                EncoderPreset,
                kind == MediaKind.Audio ? null : library.EncoderPreset,
                StringComparison.OrdinalIgnoreCase)
            && HdrHandling == rules.Hdr
            && DownmixToStereo == rules.DownmixToStereo
            && ImageDownscaleMode == rules.ImageDownscaleMode
            && ImageDownscaleValue == rules.ImageDownscaleValue
            && InitialQuality == quality;
    }
}
