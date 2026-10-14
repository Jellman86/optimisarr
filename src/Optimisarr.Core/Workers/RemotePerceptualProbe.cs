using Optimisarr.Core.Domain;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Verification;

namespace Optimisarr.Core.Workers;

/// <summary>Frozen server intent. Workers return measurements; only the server judges targets.</summary>
public sealed record RemotePerceptualSearchContract(Guid Id, MediaKind Kind, string Encoder,
    PerceptualQualityPlan Plan, VerificationPolicy Policy, IReadOnlyList<PerceptualAudioWindow> AudioWindows,
    AudioQualityInput? SourceAudio = null, ImageQualityMeasurementContext? SourceImage = null);

/// <summary>Assessment of the transferred original, before any sample encode starts.</summary>
public sealed record RemotePerceptualSourceEvidence(AudioQualityInput? Audio = null,
    RemoteImagePerceptualEvidence? Image = null, string? Error = null, ImageMetadataResult? ImageMetadata = null);

public sealed record RemotePerceptualSampleEvidence(int Index, long EncodedBytes,
    string ReferenceSha256, string CandidateSha256, RemoteAudioQualityEvidence? Audio = null,
    RemoteImagePerceptualEvidence? Image = null, LoudnessResult? SourceLoudness = null,
    LoudnessResult? CandidateLoudness = null, ImageQualityResult? Ssim = null, string? Error = null);

public sealed record RemotePerceptualProbeReport(Guid ContractId, string Encoder, int Value,
    string SourceSha256, RemotePerceptualSourceEvidence Source,
    IReadOnlyList<RemotePerceptualSampleEvidence> Samples, string? Error = null, string? EncoderSha256 = null);

public sealed record RemotePerceptualProbeValidation(PerceptualSearchProbe? Probe, bool SourceRejected, string? Error)
{
    public bool Accepted => Probe is not null;
}

/// <summary>Validates file-bound raw evidence against the assigned setting, source and coverage.</summary>
public static class RemotePerceptualProbeValidator
{
    public const long MaximumSampleBytes = 128L * 1024 * 1024;

    public static RemotePerceptualProbeValidation Validate(RemotePerceptualSearchContract contract, int asked,
        string sourceHash, RemotePerceptualProbeReport report)
    {
        if (!ValidContract(contract) || report is null || !Hash(sourceHash) || contract.Id != report.ContractId
            || contract.Encoder != report.Encoder || report.Value != asked || !contract.Plan.Values.Contains(asked)
            || !SameHash(sourceHash, report.SourceSha256))
            return Refuse("The perceptual report does not match its assigned contract, encoder, source or setting.");
        var sourceError = SourceError(contract, sourceHash, report.Source);
        if (sourceError is not null) return new(null, true, sourceError);
        var expected = contract.Kind == MediaKind.Image ? 1 : contract.AudioWindows.Count;
        if (report.Samples is null || report.Samples.Count > expected
            || (report.Error is null && report.Samples.Count != expected)
            || report.Samples.Any(s => s is null || s.Index < 0 || s.Index >= expected
                || s.EncodedBytes < 0 || s.EncodedBytes > MaximumSampleBytes
                || (s.EncodedBytes == 0 && s.Error is null && report.Error is null))
            || report.Samples.Select(s => s.Index).Distinct().Count() != report.Samples.Count)
            return Refuse("The perceptual report does not cover its assigned samples or byte budget.");
        if (ToolError(contract, report) is { } toolError) return Refuse(toolError);
        if (report.Error is not null)
            return Accept(new(asked, report.Samples.Sum(s => s.EncodedBytes), PerceptualSearchMeasurement.Unavailable(report.Error)));
        var measurements = new List<PerceptualSearchMeasurement>();
        foreach (var sample in report.Samples.OrderBy(s => s.Index))
        {
            if (sample.Error is not null)
                return Accept(new(asked, report.Samples.Sum(s => s.EncodedBytes), PerceptualSearchMeasurement.Unavailable(sample.Error)));
            if (!Hash(sample.ReferenceSha256) || !Hash(sample.CandidateSha256))
                return Refuse("Every sample must identify its actual reference and candidate files.");
            measurements.Add(contract.Kind == MediaKind.Image
                ? Image(contract, sourceHash, sample, report.Source.Image!.Assessment.Measurement!)
                : Audio(contract, sample));
        }
        var unavailable = measurements.FirstOrDefault(m => !m.Available);
        var combined = unavailable ?? (contract.Kind == MediaKind.Image ? measurements.Single()
            : new PerceptualSearchMeasurement(true, measurements.All(m => m.MeetsTarget), measurements.Max(m => m.Score), null,
                WindowMeasurements: measurements.ToArray(), LoudnessDriftLufs: measurements.Max(m => m.LoudnessDriftLufs),
                SourceTruePeakDbtp: measurements.Max(m => m.SourceTruePeakDbtp), CandidateTruePeakDbtp: measurements.Max(m => m.CandidateTruePeakDbtp),
                TargetDetail: measurements.FirstOrDefault(m => !m.MeetsTarget)?.TargetDetail,
                SourceTruePeakIsSilent: measurements.All(m => m.SourceTruePeakIsSilent == true),
                CandidateTruePeakIsSilent: measurements.All(m => m.CandidateTruePeakIsSilent == true)));
        return Accept(new(asked, report.Samples.Sum(s => s.EncodedBytes), combined));
    }

    internal static RemotePerceptualToolIdentity ToolIdentity(RemotePerceptualSearchContract contract, RemotePerceptualProbeReport report)
    {
        var audio = report.Samples.FirstOrDefault(s => s.Audio?.Assessment is { Measured: true, Error: null })?.Audio?.Assessment;
        return new(report.EncoderSha256!.ToLowerInvariant(),
            (contract.Kind == MediaKind.Image ? report.Source.Image?.Assessment?.MetricSha256 : audio?.MetricSha256)?.ToLowerInvariant(),
            audio?.FfmpegSha256?.ToLowerInvariant(), audio?.FfprobeSha256?.ToLowerInvariant());
    }

    private static string? ToolError(RemotePerceptualSearchContract contract, RemotePerceptualProbeReport report)
    {
        if (!Hash(report.EncoderSha256)) return "The sample report must identify the actual encoder binary.";
        if (contract.Kind == MediaKind.Image)
        {
            var sourceMetric = report.Source.Image!.Assessment.MetricSha256;
            if (report.Samples.Any(s => s.Image?.Assessment is { Error: null } image && !SameHash(sourceMetric, image.MetricSha256)))
                return "The original and image samples were measured by different native tools.";
        }
        else
        {
            var measured = report.Samples.Select(s => s.Audio?.Assessment).Where(a => a is { Measured: true, Error: null }).ToArray();
            if (measured.Any(a => !SameHash(report.EncoderSha256, a!.FfmpegSha256))
                || measured.Select(a => (a!.MetricSha256?.ToLowerInvariant(), a.FfmpegSha256?.ToLowerInvariant(), a.FfprobeSha256?.ToLowerInvariant())).Distinct().Count() > 1)
                return "The audio samples were encoded or measured by different native tools.";
        }
        return null;
    }

    private static PerceptualSearchMeasurement Image(RemotePerceptualSearchContract contract, string sourceHash,
        RemotePerceptualSampleEvidence sample, ImagePerceptualMeasurement original)
    {
        if (sample.Audio is not null || !SameHash(sourceHash, sample.ReferenceSha256))
            return PerceptualSearchMeasurement.Unavailable("The image sample must compare the transferred original directly.");
        if (RemoteImagePerceptualEvidence.VersionError(sample.Image) is { } versionError)
            return PerceptualSearchMeasurement.Unavailable(versionError);
        var measurement = PerceptualSearchEvidence.Image(sample.Image!.Assessment, sourceHash, sample.CandidateSha256,
            contract.Policy.MinimumImagePerceptualScore);
        if (!measurement.Available) return measurement;
        if (sample.Image!.Assessment.Measurement is not { } actual || actual.Width != original.Width || actual.Height != original.Height
            || actual.Alpha != original.Alpha)
            return PerceptualSearchMeasurement.Unavailable("The image sample changed its source dimensions or transparency contract.");
        if (!contract.Policy.ImageQualityGateEnabled) return measurement;
        if (sample.Ssim is not { Measured: true, Error: null, Ssim: { } ssim } || !double.IsFinite(ssim) || ssim is < -1 or > 1)
            return PerceptualSearchMeasurement.Unavailable(sample.Ssim?.Error ?? "The assigned SSIM measurement is incomplete.");
        return measurement with { ImageSsim = ssim, MeetsTarget = measurement.MeetsTarget && ssim >= contract.Policy.MinimumImageSsim,
            TargetDetail = ssim < contract.Policy.MinimumImageSsim ? "The sample missed the configured SSIM floor." : null };
    }

    private static PerceptualSearchMeasurement Audio(RemotePerceptualSearchContract contract, RemotePerceptualSampleEvidence sample)
    {
        if (sample.Image is not null || sample.Audio?.Assessment is not { Reference: { } reference, Candidate: { } candidate } assessment
            || assessment.ReferenceAudioIndex is not null || assessment.CandidateAudioIndex is not null
            || reference.Channels != contract.SourceAudio!.Channels || reference.ChannelLayout != contract.SourceAudio.ChannelLayout
            || reference.SampleRate != 48000 || candidate.Channels != reference.Channels
            || Math.Abs(reference.DurationSeconds - contract.AudioWindows[sample.Index].DurationSeconds) > 1.0 / 48000)
            return PerceptualSearchMeasurement.Unavailable("The audio sample does not match the assigned channel and duration contract.");
        var measurement = PerceptualSearchEvidence.Audio(new("Worker", sample.Audio, null), sample.ReferenceSha256,
            sample.CandidateSha256, contract.Policy.MaximumAudioQualityDistance);
        return PerceptualAuxiliaryTargets.Audio(measurement, sample.SourceLoudness, sample.CandidateLoudness, contract.Policy);
    }

    private static string? SourceError(RemotePerceptualSearchContract contract, string sourceHash, RemotePerceptualSourceEvidence? source)
    {
        if (source is null || source.Error is not null) return string.IsNullOrWhiteSpace(source?.Error) ? "Required source assessment is missing." : source.Error;
        if (contract.Kind == MediaKind.Image)
        {
            if (RemoteImagePerceptualEvidence.VersionError(source.Image) is { } versionError) return versionError;
            var measured = PerceptualSearchEvidence.Image(source.Image!.Assessment, sourceHash, sourceHash, contract.Policy.MinimumImagePerceptualScore);
            if (source.Audio is not null || !measured.Available) return source.Image?.Assessment?.Error ?? "Required original-image assessment is unavailable.";
            if (contract.Policy.ImageMetadataGateEnabled && source.ImageMetadata is not { Measured: true, Error: null })
                return source.ImageMetadata?.Error ?? "Required original-image metadata assessment is unavailable.";
            var image = source.Image!.Assessment.Measurement!;
            var expected = contract.SourceImage!;
            return image.Width != expected.ReferenceWidth || image.Height != expected.ReferenceHeight
                || (image.Alpha && !expected.ReferenceMayHaveAlpha) || image.MaximumAlphaError != 0
                ? "The original image does not match its frozen source profile." : null;
        }
        var audio = source.Audio;
        var assigned = contract.SourceAudio!;
        return source.Image is not null || audio is null || audio.Channels != assigned.Channels || audio.SampleRate != assigned.SampleRate
            || audio.ChannelLayout != assigned.ChannelLayout || audio.ContainerLeadSeconds != assigned.ContainerLeadSeconds
            || !double.IsFinite(audio.DurationSeconds) || Math.Abs(audio.DurationSeconds - assigned.DurationSeconds) > 1.0 / 48000
            ? "The original audio does not match its frozen source profile." : null;
    }

    internal static bool ValidContract(RemotePerceptualSearchContract contract)
    {
        if (contract is null || contract.Id == Guid.Empty || contract.Plan?.Values is not { Count: > 1 } || contract.Policy is null
            || contract.AudioWindows is null || contract.Plan.BaselineIndex < 0 || contract.Plan.BaselineIndex >= contract.Plan.Values.Count) return false;
        try
        {
            var canonical = contract.Kind == MediaKind.Image ? PerceptualQualityPlan.Image(contract.Encoder, contract.Plan.Baseline)
                : PerceptualQualityPlan.Audio(contract.Encoder, contract.Plan.Baseline);
            if (!contract.Plan.Values.SequenceEqual(canonical.Values)) return false;
        }
        catch (ArgumentException) { return false; }
        return contract.Kind == MediaKind.Image
            ? contract.Policy.ImagePerceptualGateEnabled && ImagePerceptualQualityGate.ValidLimit(contract.Policy.MinimumImagePerceptualScore)
                && contract.SourceImage is { ReferenceWidth: >= 8 and <= 16384, ReferenceHeight: >= 8 and <= 16384 } image
                && (long)image.ReferenceWidth * image.ReferenceHeight <= 16000000 && contract.SourceAudio is null && contract.AudioWindows.Count == 0
                && (!contract.Policy.ImageQualityGateEnabled || double.IsFinite(contract.Policy.MinimumImageSsim) && contract.Policy.MinimumImageSsim is >= 0 and <= 1)
            : contract.Kind == MediaKind.Audio && contract.Policy.AudioQualityGateEnabled && AudioQualityGate.ValidLimit(contract.Policy.MaximumAudioQualityDistance)
                && contract.SourceImage is null && contract.SourceAudio is { Channels: 1 or 2, SampleRate: >= 8000 and <= 384000 } audio
                && audio.ChannelLayout == (audio.Channels == 1 ? "mono" : "stereo") && audio.ContainerLeadSeconds == 0
                && contract.AudioWindows.Count > 0 && contract.AudioWindows.SequenceEqual(PerceptualSamplePlanner.AudioWindows(audio.DurationSeconds));
    }

    private static RemotePerceptualProbeValidation Refuse(string reason) => new(null, false, reason);
    private static RemotePerceptualProbeValidation Accept(PerceptualSearchProbe probe) => new(probe, false, null);
    private static bool Hash(string? hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);
    private static bool SameHash(string? expected, string? actual) => Hash(expected) && Hash(actual)
        && string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
}
