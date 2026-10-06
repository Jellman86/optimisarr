using Optimisarr.Core.Domain;
using Optimisarr.Core.Workers;

namespace Optimisarr.Core.Verification;

public sealed class SoundtrackQualityObservationService(
    Func<string, string, int, int, CancellationToken, Task<AudioQualityAssessmentResult>>? measure,
    Func<string, string, IReadOnlyList<SoundtrackQualityPair>, CancellationToken, Task<IReadOnlyList<AudioQualityAssessmentResult>>>? measureBatch = null,
    string measurementLocation = "Server")
{
    public async Task<SoundtrackQualityReport?> ObserveAsync(bool enabled, bool reencoded, bool healthy, bool preview,
        string source, string candidate, string sourceProbe, string candidateProbe, SoundtrackQualityRequest request,
        RemoteVerificationEvidence? remote, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!enabled || !reencoded || preview) return null;
        if (!healthy) return new([], "Skipped because the candidate failed decode health.");
        var plan = SoundtrackQualityPlanner.Plan(sourceProbe, candidateProbe, request);
        if (plan.Error is { } error) return new([], error);
        if (remote is not null)
            return Validate(remote.SoundtrackQuality, plan, remote.SourceSha256, remote.CandidateSha256);
        if (measure is null && measureBatch is null) return new([], "The audio assessment tool is unavailable on this host.");
        IReadOnlyList<AudioQualityAssessmentResult>? batch;
        try { batch = measureBatch is null ? null : await measureBatch(source, candidate, plan.Tracks, token); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return new([], "Audio quality could not be measured: " + ex.Message); }
        if (batch is not null && batch.Count != plan.Tracks.Count) return new([], "The host did not assess every retained soundtrack.");
        var tracks = new List<SoundtrackQualityTrack>();
        string? sourceHash = null, candidateHash = null;
        foreach (var pair in plan.Tracks)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var result = batch is null ? await measure!(source, candidate, pair.SourceAudioIndex, pair.CandidateAudioIndex, token) : batch[tracks.Count];
                var evidence = RemoteAudioQualityEvidence.From(result);
                var reason = AudioQualityReporting.Validate(evidence, result.ReferenceSha256, result.CandidateSha256, AudioQualityResultParser.SoundtrackPreparation);
                if (result.Reference != pair.Reference || result.Candidate != pair.Candidate
                    || result.ReferenceAudioIndex != pair.SourceAudioIndex || result.CandidateAudioIndex != pair.CandidateAudioIndex)
                    reason = "The measured audio profiles do not match the assigned soundtrack.";
                if (sourceHash is not null && (sourceHash != result.ReferenceSha256 || candidateHash != result.CandidateSha256))
                    return new([], "Media changed between soundtrack assessments; the scores cannot be used.");
                sourceHash = result.ReferenceSha256;
                candidateHash = result.CandidateSha256;
                tracks.Add(new(pair, new(measurementLocation, reason is null ? evidence : null, reason)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
            { tracks.Add(new(pair, new(measurementLocation, null, "Audio quality could not be measured: " + ex.Message))); }
        }
        return new(tracks, null);
    }

    public static SoundtrackQualityReport Validate(SoundtrackQualityReport? report, SoundtrackQualityPlan plan,
        string sourceHash, string candidateHash)
    {
        if (report is null) return new([], "The worker returned no soundtrack quality evidence. Update the sidecar if needed.");
        if (plan.Error is not null) return new([], plan.Error);
        if (report.UnavailableReason is not null) return new([], report.UnavailableReason);
        if (report.Tracks is null || report.Tracks.Count != plan.Tracks.Count)
            return new([], "The worker did not assess every retained soundtrack.");
        var tracks = new List<SoundtrackQualityTrack>();
        for (var index = 0; index < plan.Tracks.Count; index++)
        {
            var actual = report.Tracks[index];
            var pair = plan.Tracks[index];
            if (actual?.Track != pair || actual.Report is null)
                return new([], "The worker soundtrack mapping does not match the frozen assignment.");
            var evidence = actual.Report.Evidence;
            var error = actual.Report.UnavailableReason ?? AudioQualityReporting.Validate(evidence, sourceHash, candidateHash, AudioQualityResultParser.SoundtrackPreparation);
            if (evidence?.Assessment is not null && (evidence.Assessment.Reference != pair.Reference || evidence.Assessment.Candidate != pair.Candidate
                || evidence.Assessment.ReferenceAudioIndex != pair.SourceAudioIndex || evidence.Assessment.CandidateAudioIndex != pair.CandidateAudioIndex))
                error = "The worker measured different soundtrack profiles.";
            tracks.Add(new(pair, new("Worker", error is null ? evidence : null, error)));
        }
        return new(tracks, null);
    }
}

public static class SoundtrackQualityGate
{
    public static VerificationReport Apply(VerificationReport report, VerificationPolicy policy, bool reencoded, bool preview = false)
    {
        if (!policy.SoundtrackQualityGateEnabled || !reencoded || preview) return report;
        var soundtracks = report.SoundtrackQuality ?? new([], "Soundtrack quality was not measured.");
        var tracks = new List<SoundtrackQualityTrack>();
        var checks = new List<VerificationCheck>();
        if (soundtracks.UnavailableReason is not null || soundtracks.Tracks is not { Count: > 0 })
            checks.Add(new("Soundtrack quality (Zimtohrli)", CheckOutcome.Failed,
                (soundtracks.UnavailableReason ?? "No retained soundtrack was assessed.") + " Replacement is blocked."));
        else
            foreach (var track in soundtracks.Tracks)
            {
                var evaluated = AudioQualityGate.Apply(new([], AudioQuality: track.Report), MediaKind.Audio,
                    policy with { AudioQualityGateEnabled = true, MaximumAudioQualityDistance = policy.MaximumSoundtrackQualityDistance }, soundtrack: true);
                tracks.Add(track with { Report = evaluated.AudioQuality! });
                checks.AddRange(evaluated.Checks.Select(check => check with
                { Name = $"Soundtrack {track.Track.CandidateAudioIndex + 1}: {check.Name}" }));
            }
        return report with
        {
            Checks = [.. report.Checks, .. checks],
            SoundtrackQuality = soundtracks with { Tracks = tracks, GateEnabled = true,
                MaximumDistance = policy.MaximumSoundtrackQualityDistance, GatePassed = checks.All(check => check.Outcome == CheckOutcome.Passed) }
        };
    }
}
