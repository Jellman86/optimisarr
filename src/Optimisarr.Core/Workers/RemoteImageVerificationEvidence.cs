using Optimisarr.Core.Domain;
using Optimisarr.Core.Library;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Verification;

namespace Optimisarr.Core.Workers;

public sealed record RemoteImageVerificationRequest(bool MeasureSsim, bool MeasureMetadata, bool MeasurePerceptual);

/// <summary>Still-image measurements bound to the transferred files and the worker's native tools.</summary>
public sealed record RemoteImageVerificationEvidence(RemoteImagePerceptualEvidence? Source,
    RemoteImagePerceptualEvidence? Perceptual = null, ImageQualityResult? Ssim = null,
    ImageMetadataResult? SourceMetadata = null, ImageMetadataResult? CandidateMetadata = null,
    string? EncoderSha256 = null, string? ProbeSha256 = null, string? MetadataToolSha256 = null);

public static class RemoteImageVerificationEvidenceValidator
{
    public static IReadOnlyList<string> Validate(RemoteVerificationEvidence evidence, RemoteImageVerificationRequest? request)
    {
        var reasons = new List<string>();
        if (request is null || evidence.Image is not { } image) return ["Required still-image verification evidence is missing."];
        if (!Hash(image.EncoderSha256) || !Hash(image.ProbeSha256)) reasons.Add("The worker's image encoder and probe tools are not identified.");
        if (RemoteImagePerceptualEvidence.VersionError(image.Source) is { } sourceVersion)
            reasons.Add(sourceVersion);
        else if (!PerceptualSearchEvidence.Image(image.Source!.Assessment, evidence.SourceSha256, evidence.SourceSha256, 0).Available)
            reasons.Add("The bounded original-image assessment is incomplete or belongs to different bytes.");
        else
        {
            var original = image.Source!.Assessment.Measurement!;
            try
            {
                var probe = MediaProbeService.Parse(evidence.SourceProbe ?? "{}");
                if (!probe.Success || original.Width != probe.Width || original.Height != probe.Height || original.MaximumAlphaError != 0
                    || original.Alpha && !Optimisarr.Core.Rules.ImageSafety.MayContainAlpha(probe.PixelFormat))
                    reasons.Add("The native original-image assessment does not match the source probe.");
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException) { reasons.Add("The original-image probe could not be read."); }
        }
        if (request.MeasurePerceptual)
        {
            if (RemoteImagePerceptualEvidence.VersionError(image.Perceptual) is { } version) reasons.Add(version);
            else if (!PerceptualSearchEvidence.Image(image.Perceptual!.Assessment, evidence.SourceSha256, evidence.CandidateSha256, 0).Available)
                reasons.Add("The final native image assessment is incomplete or belongs to different bytes.");
            else if (!Same(image.Source?.Assessment.MetricSha256, image.Perceptual!.Assessment.MetricSha256))
                reasons.Add("The original and final image assessments used different native tools.");
            else if (image.Source?.Assessment.Measurement is { } original && image.Perceptual!.Assessment.Measurement is { } final
                && (original.Width != final.Width || original.Height != final.Height || original.Alpha != final.Alpha))
                reasons.Add("The final image assessment does not cover the original's complete dimensions and transparency.");
        }
        if (request.MeasureSsim && (image.Ssim is not { Measured: true, Error: null, Ssim: { } ssim }
            || !double.IsFinite(ssim) || ssim is < -1 or > 1)) reasons.Add("The requested image SSIM measurement is incomplete.");
        if (request.MeasureMetadata && (image.SourceMetadata is not { Measured: true, Error: null }
            || image.CandidateMetadata is not { Measured: true, Error: null } || !Hash(image.MetadataToolSha256)))
            reasons.Add("Both requested image metadata measurements and their native tool identity are required.");
        return reasons;
    }

    private static bool Hash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static bool Same(string? first, string? second) => Hash(first) && Hash(second)
        && string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
}
