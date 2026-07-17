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

public sealed record CalibrationSampleDto(
    int SampleNumber,
    int SampleCount,
    int DurationSeconds,
    string Url,
    double StartSeconds,
    double GainDb);

public sealed record CalibrationVariantDto(
    string Name,
    bool IsOriginal,
    IReadOnlyList<CalibrationSampleDto> Samples,
    CalibrationVariantDiagnosticsDto? Diagnostics);

public sealed record CalibrationVariantDiagnosticsDto(
    string? Profile,
    string? Codec,
    string? Container,
    int? RequestedQuality,
    string? Encoder,
    string? QualityMode,
    int? EffectiveQuality);

public sealed record CalibrationVariantResultDto(
    string Name,
    bool IsOriginal,
    string? Profile,
    string? Codec,
    string? Container,
    int? Quality,
    string Classification,
    string? Encoder,
    string? QualityMode,
    int? EffectiveQuality,
    double? EstimatedSavingPercent,
    bool Recommended);

public sealed record CalibrationResultDto(
    int? RecommendedQuality,
    string? RecommendedProfile,
    string? Encoder,
    string? QualityMode,
    int? EffectiveQuality,
    double? EstimatedSavingPercent,
    string Outcome,
    bool Applied,
    IReadOnlyList<CalibrationVariantResultDto> Variants);

public sealed record CalibrationSessionDto(
    Guid Id,
    int LibraryId,
    int MediaFileId,
    string Source,
    string MediaKind,
    string Status,
    double PreparationProgress,
    string PreparationState,
    string? Error,
    IReadOnlyList<CalibrationVariantDto> Variants,
    CalibrationResultDto? Result);

public sealed record CalibrationStream(string Path);

internal interface ICalibrationRandomizer
{
    int Next(int exclusiveMaximum);
}

internal sealed class CryptographicCalibrationRandomizer : ICalibrationRandomizer
{
    public int Next(int exclusiveMaximum) => RandomNumberGenerator.GetInt32(exclusiveMaximum);
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
        bool diagnosticsEnabled,
        bool ignoreActiveStreams,
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
            _ => BlindCalibrationPolicy.VideoPlan(duration)
        };
        if (media.MediaKind == MediaKind.Video)
        {
            plan = plan with
            {
                Settings = plan.Settings
                    .Select(setting =>
                    {
                        var presetRules = LibraryRuleResolution.ResolveVideoPreset(
                            media.Library,
                            setting.VideoProfile!.Value);
                        return setting with
                        {
                            Quality = media.Library.QualityCrf ?? presetRules.DefaultCrf!.Value
                        };
                    })
                    .Where(setting => !media.IsHdr
                        || LibraryRuleResolution.ResolveVideoPreset(
                            media.Library,
                            setting.VideoProfile!.Value).Hdr == HdrHandling.Preserve)
                    .ToList()
            };
        }
        var id = Guid.NewGuid();
        var fingerprint = Fingerprint.From(media.Library, rules, media.MediaKind, currentQuality);
        var candidates = new List<Candidate>();

        foreach (var setting in plan.Settings)
        {
            var candidateRules = setting.VideoProfile is { } profile
                ? LibraryRuleResolution.ResolveVideoPreset(media.Library, profile)
                : rules;
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
                    RequestedVideoQuality = media.MediaKind == MediaKind.Video ? setting.Quality : null,
                    RequestedRuleProfile = setting.VideoProfile,
                    RequestedAudioBitrateKbps = media.MediaKind == MediaKind.Audio ? setting.Quality : null,
                    RequestedImageQuality = media.MediaKind == MediaKind.Image ? setting.Quality : null,
                    CalibrationSessionId = id,
                    IgnoreMediaActivity = ignoreActiveStreams,
                    CalibrationClipStartSeconds = sample.StartSeconds,
                    CalibrationClipSeconds = sample.DurationSeconds,
                    EnqueuedAt = timeProvider.GetUtcNow()
                };
                db.Jobs.Add(job);
                candidates.Add(new Candidate(
                    job,
                    setting,
                    sample,
                    media.MediaKind switch
                    {
                        MediaKind.Audio => candidateRules.TargetAudioCodec,
                        MediaKind.Image => candidateRules.TargetImageFormat,
                        _ => candidateRules.TargetVideoCodec
                    },
                    candidateRules.TargetContainer));
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
            media.VideoCodec,
            Path.GetExtension(media.RelativePath).TrimStart('.'),
            media.MediaKind switch
            {
                MediaKind.Audio => rules.TargetAudioCodec,
                MediaKind.Image => rules.TargetImageFormat,
                _ => rules.TargetVideoCodec
            },
            plan,
            fingerprint,
            candidates,
            CreateVariants(plan),
            diagnosticsEnabled,
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
            // Active polling and comparison requests keep the session alive; abandoned labs are
            // reaped two hours after their last request, even if the container keeps running.
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
                    session.Status = SessionStatus.Comparing;
                }
            }

            return ToDto(session, jobs);
        }
    }

    public async Task<CalibrationSessionDto> ClassifyAsync(
        Guid id,
        IReadOnlyDictionary<string, string> classifications,
        CancellationToken cancellationToken)
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
            if (session.Status != SessionStatus.Comparing)
            {
                throw new InvalidOperationException("This blind comparison is not ready to classify.");
            }

            var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in classifications)
            {
                if (!normalized.TryAdd(pair.Key.Trim().ToUpperInvariant(), pair.Value.Trim()))
                {
                    throw new InvalidOperationException("Each anonymous sample may only be classified once.");
                }
            }
            var candidates = session.Variants.Where(variant => !variant.IsOriginal).ToList();
            if (normalized.Count != candidates.Count
                || candidates.Any(variant => !normalized.ContainsKey(variant.Name)))
            {
                throw new InvalidOperationException("Classify every anonymous candidate before revealing the result.");
            }

            foreach (var variant in session.Variants)
            {
                if (variant.IsOriginal)
                {
                    variant.Classification = CalibrationPreference.Indistinguishable;
                    continue;
                }
                if (!Enum.TryParse<CalibrationPreference>(
                        normalized[variant.Name],
                        ignoreCase: true,
                        out var classification)
                    || !string.Equals(
                        normalized[variant.Name],
                        classification.ToString(),
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "Each sample must be Indistinguishable, Acceptable, or VisiblyWorse.");
                }
                variant.Classification = classification;
            }

            var settingRatings = session.Variants
                .Where(variant => !variant.IsOriginal && variant.Setting is not null)
                .ToDictionary(
                    variant => variant.Setting!.Key,
                    variant => variant.Classification!.Value);
            session.RecommendedSetting = BlindCalibrationPolicy.Recommend(session.Plan, settingRatings);
            session.Status = SessionStatus.Revealed;
            session.Result = BuildResult(session, jobs);
            return ToDto(session, jobs);
        }
    }

    public async Task<CalibrationSessionDto> ApplyAsync(Guid id, CancellationToken cancellationToken)
    {
        _ = await GetAsync(id, cancellationToken)
            ?? throw new KeyNotFoundException("Calibration session was not found.");
        if (!_sessions.TryGetValue(id, out var session))
        {
            throw new KeyNotFoundException("Calibration session was not found.");
        }
        lock (session.Gate)
        {
            if (session.Status is not (SessionStatus.Revealed or SessionStatus.Applied)
                || session.Result is null)
            {
                throw new InvalidOperationException("Reveal the completed comparison before applying a quality.");
            }
        }
        if (session.RecommendedSetting is null)
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
            library.AudioBitrateKbps = session.RecommendedSetting.Quality;
        }
        else if (session.MediaKind == MediaKind.Image)
        {
            library.ImageQuality = session.RecommendedSetting.Quality;
        }
        else
        {
            library.RuleProfile = session.RecommendedSetting.VideoProfile!.Value;
            library.TargetVideoCodec = null;
            library.TargetContainer = null;
            if (library.RuleProfile == RuleProfile.ScottsSettings)
            {
                library.VideoAudioCodec = "aac";
                library.VideoAudioBitrateKbps = 96;
                library.DownmixToStereo = true;
                library.HdrHandling = HdrHandling.Preserve;
            }
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
        string variantName,
        int sampleIndex,
        CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            return null;
        }

        Variant? variant;
        Candidate? candidate;
        lock (session.Gate)
        {
            if (session.Status is not (SessionStatus.Comparing or SessionStatus.Revealed or SessionStatus.Applied)
                || sampleIndex < 0
                || sampleIndex >= session.Plan.Samples.Count)
            {
                return null;
            }
            variant = session.Variants.FirstOrDefault(item =>
                string.Equals(item.Name, variantName, StringComparison.Ordinal));
            if (variant is null)
            {
                return null;
            }
            var setting = variant.Setting ?? session.Plan.Settings[0];
            candidate = session.Candidates.FirstOrDefault(item =>
                item.Setting.Key == setting.Key && item.Sample.Index == sampleIndex);
        }
        if (variant is null || candidate is null) return null;

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var outputPath = await db.Jobs
            .AsNoTracking()
            .Where(job => job.Id == candidate.Job.Id
                && job.CalibrationSessionId == sessionId
                && job.Status == JobStatus.Completed)
            .Select(job => job.WorkOutputPath)
            .FirstOrDefaultAsync(cancellationToken);
        if (outputPath is null)
        {
            return null;
        }

        // Serve the verifier's short reference for the explicitly marked original variant.
        // The DTO supplies its pre-roll offset so the shared timeline still selects matching frames.
        var path = variant.IsOriginal
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

            foreach (var sample in session.Plan.Samples)
            {
                var sampleJobIds = session.Candidates
                    .Where(candidate => candidate.Sample.Index == sample.Index)
                    .Select(candidate => candidate.Job.Id)
                    .ToHashSet();
                var sampleJobs = jobs.Where(job => sampleJobIds.Contains(job.Id)).ToList();
                if (sampleJobs.Count == 0 || sampleJobs.Any(job => job.WorkOutputPath is null))
                {
                    throw new InvalidOperationException("An audio calibration candidate has no output path.");
                }

                var referencePath = Path.Combine(
                    Path.GetDirectoryName(sampleJobs[0].WorkOutputPath)!,
                    ".optimisarr-comparison-reference.flac");
                var original = await loudness.MeasureAsync(referencePath, cancellationToken);
                var candidateLevels = new List<double>();
                foreach (var job in sampleJobs)
                {
                    var candidate = await loudness.MeasureAsync(job.WorkOutputPath!, cancellationToken);
                    if (!candidate.Measured || candidate.IntegratedLufs is null)
                    {
                        throw new InvalidOperationException(
                            "The audio samples could not be level-matched, so the blind comparison was stopped.");
                    }
                    candidateLevels.Add(candidate.IntegratedLufs.Value);
                }
                if (!original.Measured || original.IntegratedLufs is null)
                {
                    throw new InvalidOperationException(
                        "The audio samples could not be level-matched, so the blind comparison was stopped.");
                }

                var gains = BlindCalibrationPolicy.MatchAudioGroupLevels(
                    [original.IntegratedLufs.Value, .. candidateLevels]);
                for (var index = 0; index < sampleJobs.Count; index++)
                {
                    session.AudioLevels[sampleJobs[index].Id] = new AudioLevelMatch(gains[0], gains[index + 1]);
                }
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

    private List<Variant> CreateVariants(BlindCalibrationPlan plan)
    {
        var settings = plan.Settings.ToList();
        for (var index = settings.Count - 1; index > 0; index--)
        {
            var swap = randomizer.Next(index + 1);
            (settings[index], settings[swap]) = (settings[swap], settings[index]);
        }
        return
        [
            new Variant("ORIGINAL", true, null),
            .. settings.Select((setting, index) => new Variant(
                ((char)('A' + index)).ToString(),
                false,
                setting))
        ];
    }

    private static CalibrationResultDto BuildResult(Session session, IReadOnlyList<Job> jobs)
    {
        CalibrationVariantResultDto ResultFor(Variant variant)
        {
            if (variant.IsOriginal)
            {
                return new CalibrationVariantResultDto(
                    variant.Name,
                    true,
                    null,
                    session.SourceCodec,
                    session.SourceContainer,
                    null,
                    variant.Classification!.Value.ToString(),
                    null,
                    null,
                    null,
                    null,
                    false);
            }

            var setting = variant.Setting!;
            var quality = setting.Quality;
            var candidateJobs = JobsForSetting(session, jobs, setting);
            var representative = candidateJobs.FirstOrDefault();
            var candidate = session.Candidates.First(item => item.Setting.Key == setting.Key);
            return new CalibrationVariantResultDto(
                variant.Name,
                false,
                setting.VideoProfile?.ToString(),
                candidate.Codec,
                representative?.WorkOutputPath is { } outputPath
                    ? Path.GetExtension(outputPath).TrimStart('.')
                    : candidate.Container,
                quality,
                variant.Classification!.Value.ToString(),
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
                EstimateSavingPercent(session, candidateJobs),
                session.RecommendedSetting?.Key == setting.Key);
        }

        var variants = session.Variants.Select(ResultFor).ToList();
        var recommended = variants.FirstOrDefault(variant => variant.Recommended);
        return new CalibrationResultDto(
            session.RecommendedSetting?.Quality,
            session.RecommendedSetting?.VideoProfile?.ToString(),
            recommended?.Encoder,
            recommended?.QualityMode,
            recommended?.EffectiveQuality,
            recommended?.EstimatedSavingPercent,
            recommended is null ? "NoAcceptableSetting" : "PreferenceFound",
            false,
            variants);
    }

    private static List<Job> JobsForSetting(
        Session session,
        IReadOnlyList<Job> jobs,
        CalibrationSetting setting) =>
        jobs.Where(job => (session.MediaKind == MediaKind.Video
                ? job.RequestedRuleProfile == setting.VideoProfile
                : (session.MediaKind == MediaKind.Audio
                    ? job.RequestedAudioBitrateKbps
                    : job.RequestedImageQuality) == setting.Quality)
            && job.OutputSizeBytes is > 0)
        .ToList();

    private static double? EstimateSavingPercent(Session session, IReadOnlyList<Job> candidateJobs)
    {
        var encodedMeasure = candidateJobs.Count == 0
            ? (double?)null
            : session.MediaKind == MediaKind.Image
                ? candidateJobs.Average(job => (double)job.OutputSizeBytes!.Value)
                : candidateJobs.Average(job =>
                    job.OutputSizeBytes!.Value / (double)(job.CalibrationClipSeconds ?? 1));
        var sourceMeasure = session.MediaKind == MediaKind.Image
            ? session.SourceSizeBytes
            : session.SourceDurationSeconds > 0
                ? session.SourceSizeBytes / session.SourceDurationSeconds
                : 0;
        return encodedMeasure is null || sourceMeasure <= 0
            ? null
            : Math.Round((1 - encodedMeasure.Value / sourceMeasure) * 100, 1);
    }

    private static CalibrationSessionDto ToDto(Session session, IReadOnlyList<Job> jobs)
    {
        var progress = jobs.Count == 0
            ? 0
            : jobs.Average(job => job.Status == JobStatus.Completed ? 1 : Math.Clamp(job.Progress, 0, 1));
        session.HighestPreparationProgress = Math.Max(session.HighestPreparationProgress, progress);
        var preparationState = jobs.Count > 0 && jobs.All(job => job.Status == JobStatus.Queued)
            ? "Waiting"
            : "Working";
        return new CalibrationSessionDto(
            session.Id,
            session.LibraryId,
            session.MediaFileId,
            session.SourceName,
            session.MediaKind.ToString(),
            session.Status.ToString(),
            Math.Round(session.HighestPreparationProgress, 3),
            preparationState,
            session.Error,
            session.Status is SessionStatus.Comparing or SessionStatus.Revealed or SessionStatus.Applied
                ? session.Variants.Select(variant => ToVariantDto(session, variant, jobs)).ToList()
                : [],
            session.Status is SessionStatus.Revealed or SessionStatus.Applied ? session.Result : null);
    }

    private static CalibrationVariantDto ToVariantDto(
        Session session,
        Variant variant,
        IReadOnlyList<Job> jobs)
    {
        var jobsById = jobs.ToDictionary(job => job.Id);
        var setting = variant.Setting ?? session.Plan.Settings[0];
        var samples = session.Plan.Samples.Select(sample =>
        {
            var candidate = session.Candidates.Single(item =>
                item.Setting.Key == setting.Key && item.Sample.Index == sample.Index);
            jobsById.TryGetValue(candidate.Job.Id, out var job);
            var levels = session.MediaKind == MediaKind.Audio
                && session.AudioLevels.TryGetValue(candidate.Job.Id, out var matched)
                    ? matched
                    : new AudioLevelMatch(0, 0);
            return new CalibrationSampleDto(
                sample.Index + 1,
                session.Plan.Samples.Count,
                sample.DurationSeconds,
                $"/api/calibration/{session.Id}/variants/{variant.Name}/samples/{sample.Index}/content",
                variant.IsOriginal ? job?.CalibrationReferenceStartSeconds ?? 0 : 0,
                variant.IsOriginal ? levels.OriginalGainDb : levels.CandidateGainDb);
        }).ToList();
        CalibrationVariantDiagnosticsDto? diagnostics = null;
        if (session.DiagnosticsEnabled)
        {
            var candidate = session.Candidates.First(item => item.Setting.Key == setting.Key);
            jobsById.TryGetValue(candidate.Job.Id, out var job);
            diagnostics = variant.IsOriginal
                ? new CalibrationVariantDiagnosticsDto(
                    null,
                    session.SourceCodec,
                    session.SourceContainer,
                    null,
                    null,
                    null,
                    null)
                : new CalibrationVariantDiagnosticsDto(
                    setting.VideoProfile?.ToString(),
                    candidate.Codec,
                    job?.WorkOutputPath is { } outputPath
                        ? Path.GetExtension(outputPath).TrimStart('.')
                        : candidate.Container,
                    setting.Quality,
                    session.MediaKind is MediaKind.Audio or MediaKind.Image
                        ? candidate.Codec
                        : job?.VideoEncoder,
                    session.MediaKind switch
                    {
                        MediaKind.Audio => "kbps",
                        MediaKind.Image => "quality",
                        _ => job?.VideoQualityMode
                    },
                    session.MediaKind is MediaKind.Audio or MediaKind.Image
                        ? setting.Quality
                        : job?.EffectiveVideoQuality);
        }
        return new CalibrationVariantDto(variant.Name, variant.IsOriginal, samples, diagnostics);
    }

    private enum SessionStatus
    {
        Preparing,
        Comparing,
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
        string? sourceCodec,
        string? sourceContainer,
        string? targetCodec,
        BlindCalibrationPlan plan,
        Fingerprint fingerprint,
        List<Candidate> candidates,
        List<Variant> variants,
        bool diagnosticsEnabled,
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
        public string? SourceCodec { get; } = sourceCodec;
        public string? SourceContainer { get; } = sourceContainer;
        public string? TargetCodec { get; } = targetCodec;
        public BlindCalibrationPlan Plan { get; } = plan;
        public Fingerprint Fingerprint { get; } = fingerprint;
        public List<Candidate> Candidates { get; } = candidates;
        public List<Variant> Variants { get; } = variants;
        public bool DiagnosticsEnabled { get; } = diagnosticsEnabled;
        public double HighestPreparationProgress { get; set; }
        public DateTimeOffset ExpiresAt { get; set; } = expiresAt;
        public SessionStatus Status { get; set; } = SessionStatus.Preparing;
        public string? Error { get; set; }
        public CalibrationSetting? RecommendedSetting { get; set; }
        public CalibrationResultDto? Result { get; set; }
        public SemaphoreSlim AudioLevelGate { get; } = new(1, 1);
        public Dictionary<int, AudioLevelMatch> AudioLevels { get; } = [];
        public bool AudioLevelsReady { get; set; }
    }

    private sealed record Candidate(
        Job Job,
        CalibrationSetting Setting,
        CalibrationSample Sample,
        string? Codec,
        string? Container);
    private sealed class Variant(string name, bool isOriginal, CalibrationSetting? setting)
    {
        public string Name { get; } = name;
        public bool IsOriginal { get; } = isOriginal;
        public CalibrationSetting? Setting { get; } = setting;
        public CalibrationPreference? Classification { get; set; }
    }

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
