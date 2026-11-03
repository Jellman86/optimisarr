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
                spec.DownmixToStereo, work.VerificationPolicy, FfmpegCommandBuilder.ResolveAudioFilter(spec).Chain is not null)
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
        await using var scratch = await OwnedScratchWorkspace.CreateAsync(scopeFactory, claims, _workRoot,
            jobId, attempt, Path.GetDirectoryName(work.Spec.OutputPath)!, "adaptive-quality", logger, token);
        var host = new SoundtrackSearchHost(scratch.Root,
            async (arguments, output, seconds, cancellation) =>
            {
                var run = await RunFfmpegAsync(jobId, output, arguments, seconds, null, false, cancellation,
                    maxCandidateBytes: 32L * 1024 * 1024, progressMap: _ => AdaptiveQualityProgress.Started);
                return (run.ExitCode, run.Error);
            },
            scratch.Capture, async path => await scratch.TryDeleteAsync(path), encodes.TrackVerification);
        return await SoundtrackSearchRunner.RunAsync(work.Original.Path, work.Spec, work.VerificationPolicy, plan, sourceHash,
            services, host, async (snapshot, cancellation) =>
            {
                await UpdatePerceptualSearchAsync(jobId, attempt, sourceHash,
                    job => job.SoundtrackSearchJson = JsonSerializer.Serialize(snapshot, ReportJsonOptions), cancellation);
                await NotifyAsync();
            }, token);
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
