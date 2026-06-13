using Optimisarr.Core.Domain;
using Optimisarr.Core.Library;
using Optimisarr.Core.Verification;

namespace Optimisarr.Api.Queue;

/// <summary>The properties of the original file a converted output is judged against.</summary>
public sealed record OriginalSnapshot(
    string Path,
    long SizeBytes,
    double? DurationSeconds,
    int AudioTrackCount,
    int SubtitleTrackCount,
    bool IsHdr,
    bool HdrConvertedToSdr,
    MediaKind Kind = MediaKind.Video,
    bool AudioReencoded = false,
    bool AudioDownmixed = false,
    bool ImageDownscaleRequested = false);

/// <summary>A completed verification: the report plus the measured output size.</summary>
public sealed record VerificationOutcome(VerificationReport Report, long OutputSizeBytes);

/// <summary>
/// Gathers the real-world evidence a converted output is healthy — a full software
/// decode and an ffprobe of the output — then hands it to the pure
/// <see cref="VerificationEvaluator"/>. This is the only place verification touches
/// the filesystem or FFmpeg; the judgement itself stays pure and testable.
/// </summary>
public sealed class VerificationService(
    MediaProbeService probe,
    DecodeHealthCheck decode,
    TimestampIntegrityCheck timestamps,
    QualityScoreService quality,
    LoudnessService loudness,
    ImageQualityService imageQuality)
{
    public async Task<VerificationOutcome> VerifyAsync(
        OriginalSnapshot original,
        string outputPath,
        VerificationPolicy policy,
        CancellationToken cancellationToken)
    {
        var decodeResult = await decode.CheckAsync(outputPath, cancellationToken);
        // Packet-timestamp integrity is a video concern; skip it for an audio output.
        var timestampResult = original.Kind == MediaKind.Audio
            ? TimestampCheckResult.NotMeasured
            : await timestamps.CheckAsync(outputPath, cancellationToken);
        var outputProbe = await probe.ProbeAsync(outputPath, cancellationToken);
        var outputSize = TryGetSize(outputPath);

        // A quick re-probe of the original (no decode) gives its audio shape so we can
        // catch a silent downmix or sample-rate drop in the output.
        var originalProbe = await probe.ProbeAsync(original.Path, cancellationToken);

        // VMAF is expensive (a second full decode of both files), so only measure it
        // when the user has opted into the quality gate.
        var qualityResult = policy.QualityGateEnabled
            ? await quality.MeasureAsync(original.Path, outputPath, cancellationToken)
            : null;

        // The image SSIM gate is the still-image counterpart of VMAF: measure it only for an
        // image job and only when the user opted in, since it runs an extra ffmpeg pass.
        var imageQualityResult = policy.ImageQualityGateEnabled && original.Kind == MediaKind.Image
            ? await imageQuality.MeasureAsync(original.Path, outputPath, cancellationToken)
            : null;

        // The loudness and clipping gates share one ebur128 decode of each file, so the
        // measurement runs when either is enabled; both are opt-in for the extra passes.
        LoudnessResult? originalLoudness = null;
        LoudnessResult? outputLoudness = null;
        if (policy.AudioLoudnessGateEnabled || policy.AudioClippingGateEnabled)
        {
            originalLoudness = await loudness.MeasureAsync(original.Path, cancellationToken);
            outputLoudness = await loudness.MeasureAsync(outputPath, cancellationToken);
        }

        var loudnessMeasured = originalLoudness is { Measured: true } && outputLoudness is { Measured: true };
        var loudnessError = originalLoudness?.Error ?? outputLoudness?.Error;

        var truePeakMeasured = loudnessMeasured
            && originalLoudness?.TruePeakDbtp is not null
            && outputLoudness?.TruePeakDbtp is not null;
        var truePeakError = loudnessMeasured
            ? "ebur128 produced no true-peak reading."
            : loudnessError;

        var input = new VerificationInput(
            DecodeSucceeded: decodeResult.Healthy,
            DecodeError: decodeResult.Error,
            DecodeErrorCount: decodeResult.ErrorCount,
            OutputProbeSucceeded: outputProbe.Success,
            OutputProbeError: outputProbe.Error,
            OutputVideoCodec: outputProbe.VideoCodec,
            OriginalSizeBytes: original.SizeBytes,
            OutputSizeBytes: outputSize,
            OriginalDurationSeconds: original.DurationSeconds,
            OutputDurationSeconds: outputProbe.DurationSeconds,
            OriginalAudioTrackCount: original.AudioTrackCount,
            OutputAudioTrackCount: outputProbe.AudioTrackCount,
            OriginalSubtitleTrackCount: original.SubtitleTrackCount,
            OutputSubtitleTrackCount: outputProbe.SubtitleTrackCount,
            OriginalIsHdr: original.IsHdr,
            OutputIsHdr: outputProbe.IsHdr,
            HdrConvertedToSdr: original.HdrConvertedToSdr,
            OriginalMaxAudioChannels: originalProbe.MaxAudioChannels,
            OutputMaxAudioChannels: outputProbe.MaxAudioChannels,
            OriginalMaxAudioSampleRate: originalProbe.MaxAudioSampleRate,
            OutputMaxAudioSampleRate: outputProbe.MaxAudioSampleRate,
            QualityMeasured: qualityResult?.Measured ?? false,
            QualityError: qualityResult?.Error,
            QualityScores: qualityResult?.Scores,
            LoudnessMeasured: loudnessMeasured,
            LoudnessError: loudnessError,
            OriginalLoudnessLufs: originalLoudness?.IntegratedLufs,
            OutputLoudnessLufs: outputLoudness?.IntegratedLufs,
            TruePeakMeasured: truePeakMeasured,
            TruePeakError: truePeakError,
            OriginalTruePeakDbtp: originalLoudness?.TruePeakDbtp,
            OutputTruePeakDbtp: outputLoudness?.TruePeakDbtp,
            OriginalColorPrimaries: originalProbe.ColorPrimaries,
            OutputColorPrimaries: outputProbe.ColorPrimaries,
            OriginalColorTransfer: originalProbe.ColorTransfer,
            OutputColorTransfer: outputProbe.ColorTransfer,
            OriginalColorSpace: originalProbe.ColorSpace,
            OutputColorSpace: outputProbe.ColorSpace,
            OutputVideoStartSeconds: outputProbe.VideoStartSeconds,
            OutputAudioStartSeconds: outputProbe.AudioStartSeconds,
            TimestampsMeasured: timestampResult.Measured,
            NonMonotonicTimestampCount: timestampResult.NonMonotonicCount,
            TimestampRegressionDetail: timestampResult.FirstRegressionDetail,
            OutputLastPresentationSeconds: timestampResult.LastPresentationSeconds,
            Kind: original.Kind,
            AudioReencoded: original.AudioReencoded,
            AudioDownmixed: original.AudioDownmixed,
            OriginalWidth: originalProbe.Width,
            OriginalHeight: originalProbe.Height,
            OutputWidth: outputProbe.Width,
            OutputHeight: outputProbe.Height,
            ImageQualityMeasured: imageQualityResult?.Measured ?? false,
            ImageQualityError: imageQualityResult?.Error,
            ImageSsim: imageQualityResult?.Ssim,
            ImageDownscaleRequested: original.ImageDownscaleRequested);

        return new VerificationOutcome(VerificationEvaluator.Evaluate(input, policy), outputSize);
    }

    private static long TryGetSize(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }
}
