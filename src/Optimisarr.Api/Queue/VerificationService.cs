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
    bool HdrConvertedToSdr);

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
    QualityScoreService quality,
    LoudnessService loudness)
{
    public async Task<VerificationOutcome> VerifyAsync(
        OriginalSnapshot original,
        string outputPath,
        VerificationPolicy policy,
        CancellationToken cancellationToken)
    {
        var decodeResult = await decode.CheckAsync(outputPath, cancellationToken);
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

        // Loudness drift only matters when audio is re-encoded; it is opt-in because
        // it adds a decode pass over each file.
        LoudnessResult? originalLoudness = null;
        LoudnessResult? outputLoudness = null;
        if (policy.AudioLoudnessGateEnabled)
        {
            originalLoudness = await loudness.MeasureAsync(original.Path, cancellationToken);
            outputLoudness = await loudness.MeasureAsync(outputPath, cancellationToken);
        }

        var loudnessMeasured = originalLoudness is { Measured: true } && outputLoudness is { Measured: true };
        var loudnessError = originalLoudness?.Error ?? outputLoudness?.Error;

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
            OriginalColorPrimaries: originalProbe.ColorPrimaries,
            OutputColorPrimaries: outputProbe.ColorPrimaries,
            OriginalColorTransfer: originalProbe.ColorTransfer,
            OutputColorTransfer: outputProbe.ColorTransfer,
            OriginalColorSpace: originalProbe.ColorSpace,
            OutputColorSpace: outputProbe.ColorSpace,
            OutputVideoStartSeconds: outputProbe.VideoStartSeconds,
            OutputAudioStartSeconds: outputProbe.AudioStartSeconds);

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
