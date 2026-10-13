using Optimisarr.Core.Domain;
using Optimisarr.Core.Verification;

namespace Optimisarr.Core.Queue;

/// <summary>Validated measurements in their native units, including the original diagnostic evidence.</summary>
public sealed record PerceptualSearchMeasurement(bool Available, bool MeetsTarget, double? Score, string? Error,
    AudioQualityReport? Audio = null, ImagePerceptualReport? Image = null,
    IReadOnlyList<PerceptualSearchMeasurement>? WindowMeasurements = null,
    double? ImageSsim = null, double? LoudnessDriftLufs = null,
    double? SourceTruePeakDbtp = null, double? CandidateTruePeakDbtp = null, string? TargetDetail = null,
    bool? SourceTruePeakIsSilent = null, bool? CandidateTruePeakIsSilent = null)
{
    public static PerceptualSearchMeasurement Unavailable(string reason) => new(false, false, null, reason);
}

public static class PerceptualSearchEvidence
{
    public static PerceptualSearchMeasurement Image(ImagePerceptualReport? report, string sourceHash, string candidateHash, double? minimum)
    {
        if (!ImagePerceptualQualityGate.ValidLimit(minimum)) return PerceptualSearchMeasurement.Unavailable("An explicit image quality target is required.");
        if (report is null || report.Error is not null || !ImagePerceptualResultParser.Valid(report.Measurement)
            || !Matches(sourceHash, report.SourceSha256) || !Matches(candidateHash, report.CandidateSha256)
            || !Hash(report.MetricSha256) || !double.IsFinite(report.ElapsedSeconds) || report.ElapsedSeconds < 0)
            return PerceptualSearchMeasurement.Unavailable(report?.Error ?? "Complete file-bound image evidence is unavailable.");
        var gated = ImagePerceptualQualityGate.Apply(new([], ImagePerceptualQuality: report), MediaKind.Image,
            VerificationPolicy.Default with { ImagePerceptualGateEnabled = true, MinimumImagePerceptualScore = minimum });
        return new(true, gated.Passed, report.Measurement!.Score, null, Image: gated.ImagePerceptualQuality);
    }

    public static PerceptualSearchMeasurement Audio(AudioQualityReport? report, string sourceHash, string candidateHash, double? maximum)
    {
        if (!AudioQualityGate.ValidLimit(maximum)) return PerceptualSearchMeasurement.Unavailable("An explicit audio difference target is required.");
        var error = report?.UnavailableReason ?? AudioQualityReporting.Validate(report?.Evidence, sourceHash, candidateHash);
        if (error is not null) return PerceptualSearchMeasurement.Unavailable(error);
        var gated = AudioQualityGate.Apply(new([], AudioQuality: report), MediaKind.Audio,
            VerificationPolicy.Default with { AudioQualityGateEnabled = true, MaximumAudioQualityDistance = maximum });
        return new(true, gated.Passed, report!.Evidence!.Assessment.WorstChannelDistance, null, Audio: gated.AudioQuality);
    }

    private static bool Hash(string? hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);
    private static bool Matches(string expected, string? actual) => Hash(expected) && Hash(actual)
        && string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
}

public sealed record PerceptualSearchProbe(int Value, long EncodedBytes, PerceptualSearchMeasurement Measurement);

public sealed record PerceptualSearchResult(IReadOnlyList<PerceptualSearchProbe> Probes, PerceptualQualityDecision Decision);

/// <summary>
/// Runs sequential measurements on the eventual encoding host. The delegate owns processes,
/// progress and scratch lifetime; this coordinator owns the bound and retains the evidence.
/// </summary>
public static class PerceptualSearchRunner
{
    public static async Task<PerceptualSearchResult> RunAsync(PerceptualQualityPlan plan,
        Func<int, int, CancellationToken, Task<PerceptualSearchProbe>> measure, CancellationToken token,
        Func<PerceptualSearchResult, CancellationToken, Task>? checkpoint = null)
    {
        plan = plan with { Values = plan.Values.ToArray() };
        var probes = new List<PerceptualSearchProbe>();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var decision = PerceptualQualitySearch.Decide(plan,
                probes.Select(p => new PerceptualQualityProbe(p.Value, p.Measurement.MeetsTarget, p.EncodedBytes)).ToArray());
            if (decision.Complete) return new(probes.ToArray(), decision);
            var asked = decision.NextValue!.Value;
            PerceptualSearchProbe result;
            try { result = await measure(asked, probes.Count, token); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                or ArgumentException or System.ComponentModel.Win32Exception)
            {
                result = new(asked, 0, PerceptualSearchMeasurement.Unavailable(ex.Message));
            }
            token.ThrowIfCancellationRequested();
            if (result.Value != asked || result.EncodedBytes < 0 || result.Measurement.Available && result.EncodedBytes == 0)
                return new(probes.ToArray(), new(null, plan.Baseline, true, "The candidate did not match its assigned setting or encoded no bytes; final verification is required."));
            probes.Add(result);
            var settled = result.Measurement.Available
                ? PerceptualQualitySearch.Decide(plan, probes.Select(p => new PerceptualQualityProbe(
                    p.Value, p.Measurement.MeetsTarget, p.EncodedBytes)).ToArray())
                : new PerceptualQualityDecision(null, plan.Baseline, true,
                    $"Quality measurement was unavailable: {result.Measurement.Error ?? "incomplete evidence"}. Use the library setting and final verification.");
            var snapshot = new PerceptualSearchResult(probes.ToArray(), settled);
            if (checkpoint is not null) await checkpoint(snapshot, token);
            if (settled.Complete) return snapshot;
        }
    }
}
