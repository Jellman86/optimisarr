using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Optimisarr.Api.Deduplication;
using Optimisarr.Api.Replacement;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Library;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Verification;
using Optimisarr.Data;

namespace Optimisarr.Api.Queue;

public sealed partial class QueueDispatcher
{
    private async Task FinishOwnedPreparationDeferredAsync(int jobId, int attempt, string reason)
    {
        await _dbLock.WaitAsync(CancellationToken.None);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            await using var write = await db.BeginDiagnosticWriteAsync(CancellationToken.None);
            var job = await db.Jobs.SingleOrDefaultAsync(j => j.Id == jobId, CancellationToken.None);
            if (job is null || !OwnedPreparationFailure.TryApply(job, attempt, reason, DateTimeOffset.UtcNow)) return;
            // Do not run media failure tracking for an ownership/reservation refusal.
            await db.SaveChangesAsync(CancellationToken.None);
            await write.CommitAsync();
        }
        finally { _dbLock.Release(); }
    }

    private async Task<JobWork?> SelectPerceptualQualityAsync(int jobId, int attempt, JobWork work, CancellationToken token)
    {
        var image = work.Spec.Kind == MediaKind.Image;
        var baseline = image ? work.Spec.ImageQuality ?? ImageTarget.DefaultQuality
            : work.Spec.AudioBitrateKbps ?? AudioTarget.DefaultBitrateKbps;
        var encoder = image ? work.Spec.ImageEncoder ?? ImageTarget.Resolve(ImageTarget.DefaultFormat).Encoder
            : work.Spec.AudioEncoder ?? AudioTarget.Resolve(AudioTarget.DefaultCodec).Encoder;
        var sourceHash = await ReadPerceptualSourceHashAsync(jobId, attempt, token);
        if (work.PerceptualSearch is { RetryValue: { } retry } previous
            && previous.Encoder == encoder && previous.Kind == work.Spec.Kind
            && previous.MinimumImageScore == work.VerificationPolicy.MinimumImagePerceptualScore
            && previous.MaximumAudioDistance == work.VerificationPolicy.MaximumAudioQualityDistance
            && (image ? work.VerificationPolicy.ImagePerceptualGateEnabled && work.Spec.ImageScaleFilter is null && !work.Spec.ImageLossless
                : work.VerificationPolicy.AudioQualityGateEnabled && !work.Spec.DownmixToStereo))
        {
            if (!string.Equals(previous.SourceSha256, sourceHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The source changed before its higher-quality retry. The original was kept.");
            await UpdatePerceptualSearchAsync(jobId, attempt, sourceHash, job =>
            {
                if (image) job.AdaptiveImageQuality = retry;
                else job.AdaptiveAudioBitrateKbps = retry;
            }, token);
            var retried = image ? work.Spec with { ImageQuality = retry } : work.Spec with { AudioBitrateKbps = retry };
            return work with { Spec = retried, Arguments = FfmpegCommandBuilder.Build(retried, work.CpuThreadLimit,
                work.VideoEncoder, OptimisedMarkerValue) };
        }
        await UpdatePerceptualSearchAsync(jobId, attempt, sourceHash, job =>
        {
            job.Status = JobStatus.Probing;
            job.Progress = AdaptiveQualityProgress.Started;
            job.AdaptiveAudioBitrateKbps = null;
            job.AdaptiveImageQuality = null;
            job.PerceptualSearchJson = null;
        }, token);
        await NotifyAsync();
        await using var scope = scopeFactory.CreateAsyncScope();
        IReadOnlyList<PerceptualAudioWindow> windows = [];
        ImagePerceptualReport? sourceImage = null;
        if (image && (!work.VerificationPolicy.ImagePerceptualGateEnabled
                || !ImagePerceptualQualityGate.ValidLimit(work.VerificationPolicy.MinimumImagePerceptualScore)
                || work.Spec.ImageScaleFilter is not null || work.Spec.ImageLossless)
            || !image && (!work.VerificationPolicy.AudioQualityGateEnabled
                || !AudioQualityGate.ValidLimit(work.VerificationPolicy.MaximumAudioQualityDistance)
                || work.Spec.DownmixToStereo))
        {
            await RejectPerceptualSourceAsync(jobId, attempt, work, encoder, baseline, sourceHash,
                "Quality-first requires its explicit quality gate and a supported unchanged picture/channel contract.", null, token);
            return null;
        }
        if (image)
        {
            // Qualify the original with the bounded native decoder before FFmpeg
            // sees it. Oversized metadata, unsupported colour/depth and huge
            // dimensions must not trigger an unbounded sample decode first.
            using (encodes.TrackVerification())
                sourceImage = await scope.ServiceProvider.GetRequiredService<ImagePerceptualObservationService>()
                    .ObserveAsync(true, MediaKind.Image, true, false, false, work.Original.Path, work.Original.Path, token);
            if (!PerceptualSearchEvidence.Image(sourceImage, sourceHash, sourceHash, work.VerificationPolicy.MinimumImagePerceptualScore).Available)
            {
                await RejectPerceptualSourceAsync(jobId, attempt, work, encoder, baseline, sourceHash,
                    sourceImage?.Error ?? "The original image cannot be assessed under the required policy.", sourceImage, token);
                return null;
            }
            if (work.VerificationPolicy.ImageMetadataGateEnabled)
            {
                var metadata = await scope.ServiceProvider.GetRequiredService<ImageMetadataService>().ReadAsync(work.Original.Path, token);
                if (!metadata.Measured)
                {
                    await RejectPerceptualSourceAsync(jobId, attempt, work, encoder, baseline, sourceHash,
                        metadata.Error ?? "Required source image metadata could not be fully inspected.", sourceImage, token);
                    return null;
                }
            }
        }
        else
        {
            var probe = await scope.ServiceProvider.GetRequiredService<IMediaProbeService>().ProbeAsync(work.Original.Path, token);
            var audio = probe.RawJson is { } json ? AudioQualityInput.Parse(json) : null;
            if (audio is null)
            {
                await RejectPerceptualSourceAsync(jobId, attempt, work, encoder, baseline, sourceHash,
                    "Audio quality-first requires one known-duration mono/stereo track. Full encoding was not started.", null, token);
                return null;
            }
            windows = PerceptualSamplePlanner.AudioWindows(audio.DurationSeconds);
        }
        PerceptualSearchResult result;
        try
        {
            var plan = image ? PerceptualQualityPlan.Image(encoder, baseline) : PerceptualQualityPlan.Audio(encoder, baseline);
            await using var scratch = await OwnedScratchWorkspace.CreateAsync(scopeFactory, claims, _workRoot,
                jobId, attempt, Path.GetDirectoryName(work.Spec.OutputPath)!, "adaptive-quality", logger, token);
            var references = new List<string>();
            var referenceLoudness = new List<LoudnessResult>();
            if (!image)
            {
                for (var index = 0; index < windows.Count; index++)
                {
                    var path = Path.Combine(scratch.Root, $"reference-{index}.wav");
                    try
                    {
                        var run = await RunFfmpegAsync(jobId, path,
                            PerceptualSamplePlanner.AudioReference(work.Original.Path, path, windows[index]),
                            windows[index].DurationSeconds, null, false, token, maxCandidateBytes: 16 * 1024 * 1024,
                            progressMap: _ => AdaptiveQualityProgress.Started);
                        if (run.ExitCode != 0) throw new InvalidOperationException(run.Error ?? "Audio reference preparation failed.");
                    }
                    finally { scratch.Capture(path); }
                    references.Add(path);
                    if (work.VerificationPolicy.AudioLoudnessGateEnabled || work.VerificationPolicy.AudioClippingGateEnabled)
                        referenceLoudness.Add(await scope.ServiceProvider.GetRequiredService<LoudnessService>().MeasureSampleAsync(path, token));
                }
            }
            result = await PerceptualSearchRunner.RunAsync(plan, async (value, candidateIndex, cancellation) =>
            {
                long bytes = 0;
                var scores = new List<PerceptualSearchMeasurement>();
                var count = image ? 1 : windows.Count;
                for (var index = 0; index < count; index++)
                {
                    var path = Path.Combine(scratch.Root, $"candidate-{candidateIndex}-{index}{Path.GetExtension(work.Spec.OutputPath)}");
                    var spec = image ? work.Spec with { OutputPath = path, ImageQuality = value }
                        : PerceptualSamplePlanner.AudioCandidate(work.Spec, path, value, windows[index]);
                    try
                    {
                        var arguments = FfmpegCommandBuilder.Build(spec, work.CpuThreadLimit, optimisedMarker: null).ToArray();
                        // These random owned leaf paths must still be absent when FFmpeg starts.
                        arguments[Array.IndexOf(arguments, "-y")] = "-n";
                        var run = await RunFfmpegAsync(jobId, path, arguments, image ? null : windows[index].DurationSeconds,
                            null, false, cancellation, maxCandidateBytes: 128L * 1024 * 1024,
                            progressMap: progress => AdaptiveQualityProgress.Map(candidateIndex,
                                index, count, AdaptiveQualityPhase.Encoding, progress));
                        if (run.ExitCode != 0 || !File.Exists(path) || new FileInfo(path).Length <= 0)
                            return new PerceptualSearchProbe(value, bytes, PerceptualSearchMeasurement.Unavailable(run.Error ?? "Sample encoding failed."));
                        if (image) await imageMarker.CopyMetadataAsync(work.Original.Path, path, cancellation);
                        bytes = checked(bytes + new FileInfo(path).Length);
                        await UpdateProgressAsync(jobId, AdaptiveQualityProgress.Map(candidateIndex, index, count, AdaptiveQualityPhase.Measuring, 0));
                        using var measurementActivity = encodes.TrackVerification();
                        var candidateHash = await FileContentIdentity.HashAsync(path, cancellation);
                        PerceptualSearchMeasurement measurement;
                        if (image)
                        {
                            var report = await scope.ServiceProvider.GetRequiredService<ImagePerceptualObservationService>()
                                .ObserveAsync(true, MediaKind.Image, true, false, false, work.Original.Path, path, cancellation);
                            measurement = PerceptualSearchEvidence.Image(report, sourceHash, candidateHash,
                                work.VerificationPolicy.MinimumImagePerceptualScore);
                            // A default SSIM floor remains an independent target during selection,
                            // as well as a final gate. Do not select a perceptually passing image
                            // which is already known to violate its saved structural floor.
                            if (measurement.Available && work.VerificationPolicy.ImageQualityGateEnabled)
                            {
                                var dimensions = report!.Measurement!;
                                var structural = await scope.ServiceProvider.GetRequiredService<ImageQualityService>()
                                    .MeasureSampleAsync(work.Original.Path, path, new(dimensions.Width, dimensions.Height, dimensions.Alpha), cancellation);
                                if (!structural.Measured || structural.Ssim is null)
                                    measurement = PerceptualSearchMeasurement.Unavailable(structural.Error ?? "Image SSIM is unavailable.");
                                else measurement = measurement with
                                {
                                    ImageSsim = structural.Ssim,
                                    MeetsTarget = measurement.MeetsTarget && structural.Ssim >= work.VerificationPolicy.MinimumImageSsim,
                                    TargetDetail = structural.Ssim < work.VerificationPolicy.MinimumImageSsim ? "The sample missed the configured SSIM floor." : null
                                };
                            }
                        }
                        else
                        {
                            var referenceHash = await FileContentIdentity.HashAsync(references[index], cancellation);
                            var report = await scope.ServiceProvider.GetRequiredService<AudioQualityObservationService>()
                                .ObserveAsync(true, MediaKind.Audio, true, false, references[index], path, null, cancellation);
                            measurement = PerceptualSearchEvidence.Audio(report, referenceHash, candidateHash,
                                work.VerificationPolicy.MaximumAudioQualityDistance);
                            if (referenceLoudness.Count > 0 && measurement.Available)
                                measurement = PerceptualAuxiliaryTargets.Audio(measurement, referenceLoudness[index],
                                    await scope.ServiceProvider.GetRequiredService<LoudnessService>().MeasureSampleAsync(path, cancellation), work.VerificationPolicy);
                        }
                        scores.Add(measurement);
                        if (!measurement.Available) return new PerceptualSearchProbe(value, bytes, measurement);
                    }
                    finally
                    {
                        scratch.Capture(path);
                        await scratch.TryDeleteAsync(path);
                    }
                }
                if (image) return new PerceptualSearchProbe(value, bytes, scores.Single());
                return new PerceptualSearchProbe(value, bytes, new(true, scores.All(s => s.MeetsTarget), scores.Max(s => s.Score), null,
                    WindowMeasurements: scores.ToArray(),
                    LoudnessDriftLufs: scores.Max(s => s.LoudnessDriftLufs),
                    SourceTruePeakDbtp: scores.Max(s => s.SourceTruePeakDbtp), CandidateTruePeakDbtp: scores.Max(s => s.CandidateTruePeakDbtp),
                    TargetDetail: scores.FirstOrDefault(s => !s.MeetsTarget)?.TargetDetail));
            }, token, async (snapshot, cancellation) =>
            {
                var retained = new PerceptualSearchSummary(work.Spec.Kind, encoder, sourceHash,
                    work.VerificationPolicy.MinimumImagePerceptualScore, work.VerificationPolicy.MaximumAudioQualityDistance,
                    windows, snapshot, SourceImageAssessment: sourceImage);
                await UpdatePerceptualSearchAsync(jobId, attempt, sourceHash,
                    job => job.PerceptualSearchJson = JsonSerializer.Serialize(retained, ReportJsonOptions), cancellation);
                await NotifyAsync();
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            result = new([], new(null, baseline, true, $"Quality search unavailable: {ex.Message}. The library setting and final verification remain required."));
        }
        token.ThrowIfCancellationRequested();
        if (!await FileContentIdentity.MatchesAsync(work.Original.Path, sourceHash, token))
            throw new InvalidOperationException("The source changed during quality search. The original was kept; create a fresh attempt.");
        var summary = new PerceptualSearchSummary(work.Spec.Kind, encoder, sourceHash,
            work.VerificationPolicy.MinimumImagePerceptualScore, work.VerificationPolicy.MaximumAudioQualityDistance, windows, result,
            SourceImageAssessment: sourceImage);
        await UpdatePerceptualSearchAsync(jobId, attempt, sourceHash, job =>
        {
            job.Status = JobStatus.Transcoding;
            job.Progress = 0;
            job.PerceptualSearchJson = JsonSerializer.Serialize(summary, ReportJsonOptions);
            if (image) job.AdaptiveImageQuality = result.Decision.SelectedValue;
            else job.AdaptiveAudioBitrateKbps = result.Decision.SelectedValue;
        }, token);
        logger.Log(result.Decision.FellBack ? LogLevel.Warning : LogLevel.Information,
            "Job {JobId}: {Kind} quality search selected {Setting} on {Encoder}. {Reason}", jobId, work.Spec.Kind,
            result.Decision.SelectedValue, encoder, result.Decision.Reason);
        await NotifyAsync();
        var selected = image ? work.Spec with { ImageQuality = result.Decision.SelectedValue }
            : work.Spec with { AudioBitrateKbps = result.Decision.SelectedValue };
        return work with { Spec = selected, Arguments = FfmpegCommandBuilder.Build(selected, work.CpuThreadLimit,
            work.VideoEncoder, OptimisedMarkerValue), PerceptualSearch = summary };
    }

    private async Task<string> ReadPerceptualSourceHashAsync(int jobId, int attempt, CancellationToken token)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
        var hash = await db.Jobs.AsNoTracking().Where(j => j.Id == jobId && j.ExecutionAttempt == attempt
            && (j.Status == JobStatus.Transcoding || j.Status == JobStatus.Probing)).Select(j => j.SourceSha256).SingleOrDefaultAsync(token);
        if (!FileContentIdentity.IsHash(hash)) throw new FileCleanupDeferredException();
        return hash!;
    }

    private async Task RejectPerceptualSourceAsync(int jobId, int attempt, JobWork work, string encoder, int baseline,
        string sourceHash, string reason, ImagePerceptualReport? sourceImage, CancellationToken token)
    {
        reason = "Required source quality assessment is unavailable. Full encoding was not started. " + reason;
        var summary = new PerceptualSearchSummary(work.Spec.Kind, encoder, sourceHash,
            work.VerificationPolicy.MinimumImagePerceptualScore, work.VerificationPolicy.MaximumAudioQualityDistance, [],
            new([], new(null, baseline, true, reason)), PreparationBlocked: true, SourceImageAssessment: sourceImage);
        var report = new VerificationReport([new("Quality-first source eligibility", CheckOutcome.Failed, reason)], PerceptualSearch: summary);
        await UpdatePerceptualSearchAsync(jobId, attempt, sourceHash, job =>
        {
            job.Status = JobStatus.Failed;
            job.ErrorMessage = reason;
            job.PerceptualSearchJson = JsonSerializer.Serialize(summary, ReportJsonOptions);
            job.VerificationReportJson = JsonSerializer.Serialize(report, ReportJsonOptions);
            job.VerificationPassed = false;
            job.FinishedAt = DateTimeOffset.UtcNow;
        }, token);
        // Unsupported measurement/tool availability is not a failed media encode.
        // Retain the source without spending failure/auto-exclusion counters.
        logger.LogWarning("Job {JobId}: {Reason}", jobId, reason);
        await NotifyAsync();
    }

    internal static PerceptualSearchSummary? ReadPerceptualRetry(Job job)
    {
        if (job.RetryReason != "HigherPerceptualQualityRetry" || job.QualityRetryCount != 1 || job.PerceptualSearchJson is null) return null;
        try
        {
            var summary = JsonSerializer.Deserialize<PerceptualSearchSummary>(job.PerceptualSearchJson, ReportJsonOptions);
            if (summary is not { RetryValue: { } retry, PreparationBlocked: false, Result: { Probes.Count: > 0, Decision.FellBack: false } }) return null;
            var baseline = summary.Result.Probes[0].Value;
            var plan = summary.Kind == MediaKind.Image ? PerceptualQualityPlan.Image(summary.Encoder, baseline)
                : summary.Kind == MediaKind.Audio ? PerceptualQualityPlan.Audio(summary.Encoder, baseline) : null;
            return plan is not null && plan.Values.Contains(retry) && retry > summary.Result.Decision.SelectedValue
                && FileContentIdentity.IsHash(summary.SourceSha256) ? summary : null;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException) { return null; }
    }

    private async Task QueuePerceptualRetryAsync(int jobId, int attempt, string output, PerceptualSearchSummary summary)
    {
        if (!await RetryAfterCleanupAsync(jobId, attempt, output, job =>
        {
            JobAttemptHistory.Archive(job, "HigherPerceptualQualityRetry", DateTimeOffset.UtcNow, outcome: "Rejected");
            job.RetryReason = "HigherPerceptualQualityRetry";
            job.QualityRetryCount++;
            job.PerceptualSearchJson = JsonSerializer.Serialize(summary, ReportJsonOptions);
            job.Status = JobStatus.Queued;
            job.Progress = 0;
            job.ErrorMessage = null;
            job.FailureCategory = null;
            job.ProcessLog = null;
            job.WorkOutputPath = null;
            job.OutputSizeBytes = null;
            job.VerificationPassed = null;
            job.SourceSha256 = null;
            job.VerifiedSourceSha256 = null;
            job.VerifiedOutputSha256 = null;
            job.VerificationReportJson = null;
            job.VerifiedAt = null;
            job.FinishedAt = null;
            job.UpdatedAt = DateTimeOffset.UtcNow;
        }, CancellationToken.None)) return;
        logger.LogInformation("Job {JobId}: only final perceptual quality failed; queued one higher-fidelity retry at {Setting}.", jobId, summary.RetryValue);
        await NotifyAsync();
        Wake();
    }

    private async Task UpdatePerceptualSearchAsync(int jobId, int attempt, string sourceHash, Action<Job> mutate, CancellationToken token)
    {
        await _dbLock.WaitAsync(token);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OptimisarrDbContext>();
            await using var write = await db.BeginDiagnosticWriteAsync(token);
            var job = await db.Jobs.SingleOrDefaultAsync(j => j.Id == jobId, token);
            if (job is null || job.ExecutionAttempt != attempt || job.Status is not (JobStatus.Probing or JobStatus.Transcoding)
                || !string.Equals(job.SourceSha256, sourceHash, StringComparison.OrdinalIgnoreCase))
                throw new FileCleanupDeferredException();
            mutate(job);
            job.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(token);
            await write.CommitAsync();
        }
        finally { _dbLock.Release(); }
    }
}
