using Optimisarr.Core.Library;
using Optimisarr.Core.Verification;

namespace Optimisarr.Api.Queue;

/// <summary>The properties of the original file a converted output is judged against.</summary>
public sealed record OriginalSnapshot(
    long SizeBytes,
    double? DurationSeconds,
    int AudioTrackCount,
    int SubtitleTrackCount);

/// <summary>A completed verification: the report plus the measured output size.</summary>
public sealed record VerificationOutcome(VerificationReport Report, long OutputSizeBytes);

/// <summary>
/// Gathers the real-world evidence a converted output is healthy — a full software
/// decode and an ffprobe of the output — then hands it to the pure
/// <see cref="VerificationEvaluator"/>. This is the only place verification touches
/// the filesystem or FFmpeg; the judgement itself stays pure and testable.
/// </summary>
public sealed class VerificationService(MediaProbeService probe, DecodeHealthCheck decode)
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

        var input = new VerificationInput(
            DecodeSucceeded: decodeResult.Healthy,
            DecodeError: decodeResult.Error,
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
            OutputSubtitleTrackCount: outputProbe.SubtitleTrackCount);

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
