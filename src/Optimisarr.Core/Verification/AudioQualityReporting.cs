using Optimisarr.Core.Domain;

namespace Optimisarr.Core.Verification;

/// <summary>Versioned, file-bound measurements used for reports and an explicitly enabled gate.</summary>
public sealed record RemoteAudioQualityEvidence(string Metric, string Revision, string Preparation,
    AudioQualityAssessmentResult Assessment)
{
    public static RemoteAudioQualityEvidence From(AudioQualityAssessmentResult result) =>
        new(result.Metric, result.Revision, result.Preparation, result);
}

public sealed record AudioQualityReport(string MeasurementLocation, RemoteAudioQualityEvidence? Evidence,
    string? UnavailableReason, bool GateEnabled = false, double? MaximumDistance = null, bool? GatePassed = null);

public static class AudioQualityReporting
{
    public static bool ShouldMeasureLocally(bool enabled, MediaKind kind, bool healthy, bool clip, bool remote) =>
        enabled && kind == MediaKind.Audio && healthy && !clip && !remote;

    public static string? Validate(RemoteAudioQualityEvidence? evidence, string? referenceHash, string? candidateHash)
    {
        if (evidence?.Assessment is not { } result) return "The worker returned no audio quality report.";
        if (!result.Measured) return result.Error ?? "Audio quality could not be measured.";
        if (evidence.Metric != "zimtohrli" || evidence.Revision != AudioQualityResultParser.Revision
            || evidence.Preparation != AudioQualityResultParser.Preparation)
            return "The audio metric or preparation does not match this release.";
        if (result.Error is not null || result.Reference is not { } reference || result.Candidate is not { } candidate
            || !ValidInput(reference) || !ValidInput(candidate) || AudioQualityInput.Incompatibility(reference, candidate) is not null
            || !double.IsFinite(result.ElapsedSeconds) || result.ElapsedSeconds < 0)
            return "The audio profiles or measurement status are incomplete.";
        if (!Matches(referenceHash, result.ReferenceSha256) || !Matches(candidateHash, result.CandidateSha256)
            || !Hash(result.MetricSha256) || !Hash(result.FfmpegSha256) || !Hash(result.FfprobeSha256))
            return "The audio report is not bound to the delivered files and tools.";
        var windows = AudioQualityWindowPlanner.Plan(Math.Min(reference.DurationSeconds, candidate.DurationSeconds));
        if (result.Windows is null || result.Windows.Count != windows.Count)
            return "The audio report does not contain every assigned sample.";
        for (var index = 0; index < windows.Count; index++)
        {
            var actual = result.Windows[index];
            var expected = windows[index];
            if (actual?.Window is null || actual.Distances?.ChannelDistances is null
                || !double.IsFinite(actual.Window.StartSeconds) || !double.IsFinite(actual.Window.DurationSeconds)
                || Math.Abs(actual.Window.StartSeconds - expected.StartSeconds) > 1.0 / 48000
                || Math.Abs(actual.Window.DurationSeconds - expected.DurationSeconds) > 1.0 / 48000
                || Math.Abs(actual.Distances.Frames - expected.DurationSeconds * 48000) > 64
                || actual.Distances.ChannelDistances.Count != reference.Channels
                || actual.Distances.ChannelDistances.Any(distance => !double.IsFinite(distance) || distance is < 0 or > 1))
                return "The audio samples or channel measurements are incomplete.";
        }
        return null;
    }

    private static bool ValidInput(AudioQualityInput input) => input.Channels is 1 or 2
        && input.SampleRate is >= 8000 and <= 384000
        && input.ChannelLayout == (input.Channels == 1 ? "mono" : "stereo")
        && AudioQualityWindowPlanner.Plan(input.DurationSeconds).Count > 0;
    private static bool Hash(string? hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);
    private static bool Matches(string? expected, string? actual) => Hash(expected) && Hash(actual)
        && string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
}
