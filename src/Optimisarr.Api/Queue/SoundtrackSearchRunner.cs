using Optimisarr.Api.Replacement;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Verification;

namespace Optimisarr.Api.Queue;

/// <summary>Where the search puts its disposable files, and how it runs FFmpeg into them.</summary>
internal sealed record SoundtrackSearchHost(
    string ScratchRoot,
    Func<IReadOnlyList<string>, string, int, CancellationToken, Task<(int ExitCode, string? Error)>> RunFfmpeg,
    Action<string> Capture,
    Func<string, Task> Delete,
    Func<IDisposable> TrackMeasurement);

/// <summary>
/// The per-soundtrack bitrate search, shared by a local encode and by worker-bound preparation so
/// both measure exactly the same samples against the same limit.
/// </summary>
internal static class SoundtrackSearchRunner
{
    public static async Task<SoundtrackSearchSummary> RunAsync(string source, TranscodeSpec spec, VerificationPolicy policy,
        SoundtrackSearchPlan plan, string sourceSha256, IServiceProvider services, SoundtrackSearchHost host,
        Func<SoundtrackSearchSummary, CancellationToken, Task>? checkpoint, CancellationToken token)
    {
        var bitrates = plan.Bitrates ?? throw new ArgumentException("A soundtrack search needs a bitrate plan.", nameof(plan));
        var results = new List<SoundtrackTrackSearchResult>();
        foreach (var track in plan.Tracks)
        {
            var references = new List<string>();
            var referenceLoudness = new List<LoudnessResult>();
            try
            {
                for (var index = 0; index < track.Windows.Count; index++)
                {
                    var path = Path.Combine(host.ScratchRoot, $"reference-{track.OutputIndex}-{index}.wav");
                    try
                    {
                        var run = await host.RunFfmpeg(
                            PerceptualSamplePlanner.SoundtrackReference(source, path, track.SourceAudioIndex, track.Windows[index]),
                            path, track.Windows[index].DurationSeconds, token);
                        if (run.ExitCode != 0) throw new InvalidOperationException(run.Error ?? "Soundtrack reference preparation failed.");
                    }
                    finally { host.Capture(path); }
                    references.Add(path);
                    if (policy.AudioLoudnessGateEnabled || policy.AudioClippingGateEnabled)
                        referenceLoudness.Add(await services.GetRequiredService<LoudnessService>().MeasureSampleAsync(path, token));
                }

                var result = await PerceptualSearchRunner.RunAsync(bitrates, (value, candidateIndex, cancellation) =>
                    MeasureAsync(source, spec, policy, plan, track, value, candidateIndex, references, referenceLoudness, services, host, cancellation),
                    token, checkpoint is null ? null : (snapshot, cancellation) => checkpoint(
                        new(plan.Encoder, bitrates.Baseline, policy.MaximumSoundtrackQualityDistance, sourceSha256,
                            [.. results, new(track, snapshot)], null), cancellation));
                results.Add(new(track, result));
            }
            finally
            {
                foreach (var reference in references) await host.Delete(reference);
            }
        }
        return new(plan.Encoder, bitrates.Baseline, policy.MaximumSoundtrackQualityDistance, sourceSha256, results, null);
    }

    private static async Task<PerceptualSearchProbe> MeasureAsync(string source, TranscodeSpec spec, VerificationPolicy policy,
        SoundtrackSearchPlan plan, SoundtrackSearchTrack track, int value, int candidateIndex, IReadOnlyList<string> references,
        IReadOnlyList<LoudnessResult> referenceLoudness, IServiceProvider services, SoundtrackSearchHost host, CancellationToken token)
    {
        long bytes = 0;
        var scores = new List<PerceptualSearchMeasurement>();
        for (var index = 0; index < track.Windows.Count; index++)
        {
            var path = Path.Combine(host.ScratchRoot,
                $"candidate-{track.OutputIndex}-{candidateIndex}-{index}{PerceptualSamplePlanner.SoundtrackSampleExtension(plan.Encoder!)}");
            try
            {
                var run = await host.RunFfmpeg(
                    PerceptualSamplePlanner.SoundtrackCandidate(spec with { InputPath = source }, path, track.SourceAudioIndex, value, track.Windows[index]),
                    path, track.Windows[index].DurationSeconds, token);
                if (run.ExitCode != 0 || !File.Exists(path) || new FileInfo(path).Length <= 0)
                    return new PerceptualSearchProbe(value, Math.Max(1, bytes),
                        PerceptualSearchMeasurement.Unavailable(run.Error ?? "Soundtrack sample encoding failed."));
                bytes = checked(bytes + new FileInfo(path).Length);
                using var measurementActivity = host.TrackMeasurement();
                var referenceHash = await FileContentIdentity.HashAsync(references[index], token);
                var candidateHash = await FileContentIdentity.HashAsync(path, token);
                var report = await services.GetRequiredService<AudioQualityObservationService>()
                    .ObserveAsync(true, MediaKind.Audio, true, false, references[index], path, null, token);
                var measurement = PerceptualSearchEvidence.Audio(report, referenceHash, candidateHash, policy.MaximumSoundtrackQualityDistance);
                if (referenceLoudness.Count > 0 && measurement.Available)
                    measurement = PerceptualAuxiliaryTargets.Audio(measurement, referenceLoudness[index],
                        await services.GetRequiredService<LoudnessService>().MeasureSampleAsync(path, token), policy);
                scores.Add(measurement);
                if (!measurement.Available) return new PerceptualSearchProbe(value, bytes, measurement);
            }
            finally
            {
                host.Capture(path);
                await host.Delete(path);
            }
        }
        return new PerceptualSearchProbe(value, bytes, new(true, scores.All(s => s.MeetsTarget), scores.Max(s => s.Score), null,
            WindowMeasurements: scores.ToArray(),
            LoudnessDriftLufs: scores.Max(s => s.LoudnessDriftLufs),
            SourceTruePeakDbtp: scores.Max(s => s.SourceTruePeakDbtp), CandidateTruePeakDbtp: scores.Max(s => s.CandidateTruePeakDbtp),
            TargetDetail: scores.FirstOrDefault(s => !s.MeetsTarget)?.TargetDetail));
    }
}
