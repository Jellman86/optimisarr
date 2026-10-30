using System.Text.Json;
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
    /// <summary>
    /// Chooses each re-encoded soundtrack's bitrate by measurement before the full encode. Unlike
    /// standalone audio this never stops the job: when selection cannot run, every track keeps the
    /// library bitrate, and the soundtrack gate judges the finished file exactly as before.
    /// </summary>
    internal async Task<JobWork> SelectSoundtrackQualityAsync(int jobId, int attempt, JobWork work,
        ICollection<IDisposable> reservations, CancellationToken token)
    {
        var sourceHash = await ReadPerceptualSourceHashAsync(jobId, attempt, token);
        var spec = work.Spec;
        var baseline = spec.AudioBitrateKbps ?? AudioTarget.DefaultVideoAudioBitrateKbps;
        var maximum = work.VerificationPolicy.MaximumSoundtrackQualityDistance;
        SoundtrackSearchSummary Unavailable(string reason) =>
            new(spec.AudioEncoder, baseline, maximum, sourceHash, [], reason + " Every soundtrack keeps the library bitrate.");

        await using var scope = scopeFactory.CreateAsyncScope();
        var probe = await scope.ServiceProvider.GetRequiredService<IMediaProbeService>().ProbeAsync(work.Original.Path, token);
        var plan = probe is { Success: true, RawJson: { } json }
            ? SoundtrackSearchPlanner.Plan(json, spec.RemoveAudioStreamIndexes, spec.AudioEncoder, spec.AudioBitrateKbps,
                spec.DownmixToStereo, work.VerificationPolicy)
            : new SoundtrackSearchPlan(spec.AudioEncoder, null, [], "The source could not be probed.");

        SoundtrackSearchSummary summary;
        if (plan.Unavailable is { } refusal || plan.Bitrates is null) summary = Unavailable(plan.Unavailable ?? "No bitrate plan.");
        else if (perceptualResources.ReservePeak(MediaKind.Audio, _workRoot) is not { } peak)
            summary = Unavailable("Not enough known memory for soundtrack assessment.");
        else
        {
            reservations.Add(peak);
            await UpdatePerceptualSearchAsync(jobId, attempt, sourceHash, job =>
            {
                job.Status = JobStatus.Probing;
                job.Progress = AdaptiveQualityProgress.Started;
                job.SoundtrackSearchJson = null;
            }, token);
            await NotifyAsync();
            try
            {
                summary = await SearchSoundtracksAsync(jobId, attempt, work, plan, sourceHash, scope.ServiceProvider, token);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                summary = Unavailable("Soundtrack assessment failed: " + error.Message);
            }
        }

        token.ThrowIfCancellationRequested();
        await FileContentIdentity.RequireMatchAsync(work.Original.Path, sourceHash,
            "The source changed or became unavailable during soundtrack selection.", token);
        await UpdatePerceptualSearchAsync(jobId, attempt, sourceHash, job =>
        {
            job.Status = JobStatus.Transcoding;
            job.Progress = 0;
            job.SoundtrackSearchJson = JsonSerializer.Serialize(summary, ReportJsonOptions);
        }, token);
        var rates = summary.SelectedBitrates();
        logger.Log(rates is null ? LogLevel.Warning : LogLevel.Information,
            "Job {JobId}: soundtrack selection {Outcome}", jobId,
            rates is null ? summary.Unavailable : $"chose {string.Join(", ", rates.Select(rate => $"{rate} kbps"))} on {summary.Encoder}");
        await NotifyAsync();
        return RebuildWorkWithSpec(work, spec with { AudioTrackBitratesKbps = rates }) with { SoundtrackSearch = summary };
    }

    private async Task<SoundtrackSearchSummary> SearchSoundtracksAsync(int jobId, int attempt, JobWork work,
        SoundtrackSearchPlan plan, string sourceHash, IServiceProvider services, CancellationToken token)
    {
        var policy = work.VerificationPolicy;
        var bitrates = plan.Bitrates!;
        var results = new List<SoundtrackTrackSearchResult>();
        await using var scratch = await OwnedScratchWorkspace.CreateAsync(scopeFactory, claims, _workRoot,
            jobId, attempt, Path.GetDirectoryName(work.Spec.OutputPath)!, "adaptive-quality", logger, token);
        foreach (var track in plan.Tracks)
        {
            var references = new List<string>();
            var referenceLoudness = new List<LoudnessResult>();
            for (var index = 0; index < track.Windows.Count; index++)
            {
                var path = Path.Combine(scratch.Root, $"reference-{track.OutputIndex}-{index}.wav");
                try
                {
                    var run = await RunFfmpegAsync(jobId, path,
                        PerceptualSamplePlanner.SoundtrackReference(work.Original.Path, path, track.SourceAudioIndex, track.Windows[index]),
                        track.Windows[index].DurationSeconds, null, false, token, maxCandidateBytes: 32 * 1024 * 1024,
                        progressMap: _ => AdaptiveQualityProgress.Started);
                    if (run.ExitCode != 0) throw new InvalidOperationException(run.Error ?? "Soundtrack reference preparation failed.");
                }
                finally { scratch.Capture(path); }
                references.Add(path);
                if (policy.AudioLoudnessGateEnabled || policy.AudioClippingGateEnabled)
                    referenceLoudness.Add(await services.GetRequiredService<LoudnessService>().MeasureSampleAsync(path, token));
            }

            var result = await PerceptualSearchRunner.RunAsync(bitrates, async (value, candidateIndex, cancellation) =>
            {
                long bytes = 0;
                var scores = new List<PerceptualSearchMeasurement>();
                for (var index = 0; index < track.Windows.Count; index++)
                {
                    var path = Path.Combine(scratch.Root,
                        $"candidate-{track.OutputIndex}-{candidateIndex}-{index}{PerceptualSamplePlanner.SoundtrackSampleExtension(plan.Encoder!)}");
                    try
                    {
                        var run = await RunFfmpegAsync(jobId, path,
                            PerceptualSamplePlanner.SoundtrackCandidate(work.Spec, path, track.SourceAudioIndex, value, track.Windows[index]),
                            track.Windows[index].DurationSeconds, null, false, cancellation, maxCandidateBytes: 32L * 1024 * 1024,
                            progressMap: _ => AdaptiveQualityProgress.Started);
                        if (run.ExitCode != 0 || !File.Exists(path) || new FileInfo(path).Length <= 0)
                            return new PerceptualSearchProbe(value, Math.Max(1, bytes),
                                PerceptualSearchMeasurement.Unavailable(run.Error ?? "Soundtrack sample encoding failed."));
                        bytes = checked(bytes + new FileInfo(path).Length);
                        using var measurementActivity = encodes.TrackVerification();
                        var referenceHash = await FileContentIdentity.HashAsync(references[index], cancellation);
                        var candidateHash = await FileContentIdentity.HashAsync(path, cancellation);
                        var report = await services.GetRequiredService<AudioQualityObservationService>()
                            .ObserveAsync(true, MediaKind.Audio, true, false, references[index], path, null, cancellation);
                        var measurement = PerceptualSearchEvidence.Audio(report, referenceHash, candidateHash,
                            policy.MaximumSoundtrackQualityDistance);
                        if (referenceLoudness.Count > 0 && measurement.Available)
                            measurement = PerceptualAuxiliaryTargets.Audio(measurement, referenceLoudness[index],
                                await services.GetRequiredService<LoudnessService>().MeasureSampleAsync(path, cancellation), policy);
                        scores.Add(measurement);
                        if (!measurement.Available) return new PerceptualSearchProbe(value, bytes, measurement);
                    }
                    finally
                    {
                        scratch.Capture(path);
                        await scratch.TryDeleteAsync(path);
                    }
                }
                return new PerceptualSearchProbe(value, bytes, new(true, scores.All(s => s.MeetsTarget), scores.Max(s => s.Score), null,
                    WindowMeasurements: scores.ToArray(),
                    LoudnessDriftLufs: scores.Max(s => s.LoudnessDriftLufs),
                    SourceTruePeakDbtp: scores.Max(s => s.SourceTruePeakDbtp), CandidateTruePeakDbtp: scores.Max(s => s.CandidateTruePeakDbtp),
                    TargetDetail: scores.FirstOrDefault(s => !s.MeetsTarget)?.TargetDetail));
            }, token, async (snapshot, cancellation) =>
            {
                var progress = new SoundtrackSearchSummary(plan.Encoder, bitrates.Baseline, policy.MaximumSoundtrackQualityDistance,
                    sourceHash, [.. results, new(track, snapshot)], null);
                await UpdatePerceptualSearchAsync(jobId, attempt, sourceHash,
                    job => job.SoundtrackSearchJson = JsonSerializer.Serialize(progress, ReportJsonOptions), cancellation);
                await NotifyAsync();
            });
            results.Add(new(track, result));
            foreach (var reference in references) await scratch.TryDeleteAsync(reference);
        }
        return new(plan.Encoder, bitrates.Baseline, policy.MaximumSoundtrackQualityDistance, sourceHash, results, null);
    }

    private static JobWork RebuildWorkWithSpec(JobWork work, TranscodeSpec spec)
    {
        var primary = FfmpegCommandBuilder.Build(spec, work.CpuThreadLimit, work.VideoEncoder, OptimisedMarkerValue,
            work.HardwareDecodeRequested, work.HardwareToneMapRequested);
        var software = FfmpegCommandBuilder.Build(spec, work.CpuThreadLimit, work.VideoEncoder, OptimisedMarkerValue,
            hardwareDecode: false, hardwareToneMap: false);
        return work with
        {
            Spec = spec,
            Arguments = primary,
            SoftwareFallbackArguments = primary.SequenceEqual(software) ? null : software,
            UsedHardwareDecode = !primary.SequenceEqual(software)
        };
    }
}
