using System.Security.Cryptography;
using Optimisarr.Core.Tools;
using Optimisarr.Core.Verification;

namespace Optimisarr.Core.Workers;

/// <summary>Native still-image reads, with source qualification before FFmpeg and no local-server fallback.</summary>
public sealed class RemoteImageVerificationService(string ffmpeg, string ffprobe, string metric, string exiftool)
{
    public async Task<RemoteVerificationEvidence> MeasureAsync(string source, string candidate, RemoteVerificationContract contract,
        string sourceHash, string candidateHash, CancellationToken token)
    {
        var evidence = new RemoteVerificationEvidence(contract.Id, sourceHash, candidateHash);
        try
        {
            token.ThrowIfCancellationRequested();
            if (contract.Version != 4 || contract.Image is null || contract.MeasureAudio || contract.MeasureAudioQuality
                || contract.SoundtrackQuality is not null || contract.CountVideoFrames)
                throw new InvalidOperationException("Unsupported still-image verification contract.");
            var scorer = new ImagePerceptualQualityService(metric);
            var original = await scorer.MeasureAsync(source, source, token);
            evidence = evidence with { Image = new(RemoteImagePerceptualEvidence.From(original)) };
            if (original.Error is not null || !Same(sourceHash, original.SourceSha256))
                return evidence with { Error = original.Error ?? "The original changed before its bounded image assessment." };
            var metadataHash = contract.Image.MeasureMetadata ? await HashAsync(exiftool, token) : null;
            var metadata = new ImageMetadataService(exiftool);
            if (contract.Image.MeasureMetadata)
            {
                var originalMetadata = await metadata.ReadAsync(source, token);
                evidence = evidence with { Image = evidence.Image! with { SourceMetadata = originalMetadata } };
                if (!originalMetadata.Measured) return evidence with { Error = originalMetadata.Error ?? "Required source metadata could not be inspected." };
            }
            // Qualify the complete candidate before an ordinary decoder sees it as well. This also
            // supplies the final perceptual comparison when requested; search probes are never reused.
            var final = await scorer.MeasureAsync(source, candidate, token);
            evidence = evidence with { Image = evidence.Image! with { Perceptual = RemoteImagePerceptualEvidence.From(final) } };
            if (final.Error is not null || !Same(sourceHash, final.SourceSha256) || !Same(candidateHash, final.CandidateSha256))
                return evidence with { Error = final.Error ?? "Image verification does not match the transferred files." };
            var encoderHash = await HashAsync(ffmpeg, token);
            var probeHash = await HashAsync(ffprobe, token);
            evidence = evidence with
            {
                SourceProbe = await ProbeAsync(source, token), CandidateProbe = await ProbeAsync(candidate, token),
                Decode = await DecodeAsync(candidate, token),
                Image = evidence.Image! with { EncoderSha256 = encoderHash, ProbeSha256 = probeHash, MetadataToolSha256 = metadataHash }
            };
            var context = new ImageQualityMeasurementContext(original.Measurement!.Width, original.Measurement.Height, original.Measurement.Alpha);
            evidence = evidence with { Image = evidence.Image! with
            {
                Ssim = contract.Image.MeasureSsim ? await new ImageQualityService(ffmpeg).MeasureSampleAsync(source, candidate, context, token) : null,
                CandidateMetadata = contract.Image.MeasureMetadata ? await metadata.ReadAsync(candidate, token) : null
            } };
            if (!Same(encoderHash, await HashAsync(ffmpeg, token)) || !Same(probeHash, await HashAsync(ffprobe, token))
                || metadataHash is not null && !Same(metadataHash, await HashAsync(exiftool, token))
                || !Same(final.MetricSha256, await HashAsync(metric, token))
                || !Same(sourceHash, await HashAsync(source, token)) || !Same(candidateHash, await HashAsync(candidate, token)))
                return evidence with { Error = "A file or native tool changed during image verification." };
            var missing = RemoteVerificationEvidenceValidator.Validate(contract, evidence, sourceHash, candidateHash);
            return evidence with { Error = missing.Count == 0 ? null : "Image verification could not complete: " + string.Join(" ", missing) };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return evidence with { Error = "Image verification could not complete: " + ex.Message }; }
    }

    private async Task<string> ProbeAsync(string path, CancellationToken token)
    {
        var result = await BoundedToolProcess.RunAsync(ffprobe, ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", path], token);
        if (result.ExitCode != 0) throw new InvalidOperationException("Image probe failed: " + result.Error);
        return result.Output;
    }

    private async Task<DecodeHealthResult> DecodeAsync(string path, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        try { return await new DecodeHealthCheck(ffmpeg).CheckAsync(path, deadline.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return DecodeHealthResult.Unhealthy("Image decode timed out."); }
    }

    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        if (stream.Length > 512L * 1024 * 1024) throw new IOException("Image verification file or tool exceeds its hash budget.");
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
    }
    private static bool Same(string? first, string? second) => first is { Length: 64 } && second is { Length: 64 }
        && string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
}
