using System.Diagnostics;
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
    bool ImageDownscaleRequested = false,
    bool VideoReencoded = true,
    string? ExpectedVideoCodec = null,
    // Audio-relative indexes the kept-languages rule removed on purpose; verification expects
    // exactly those tracks gone and judges channel/sample-rate fidelity against the kept ones.
    IReadOnlyList<int>? RemovedAudioStreamIndexes = null);

/// <summary>A completed verification: the report plus the measured output size.</summary>
public sealed record VerificationOutcome(VerificationReport Report, long OutputSizeBytes);

/// <summary>The preview clip window to use when building a verification reference segment.</summary>
public sealed record VerificationClip(int Seconds, int? StartSeconds, string ReferencePath);

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
    ImageQualityService imageQuality,
    ImageMetadataService imageMetadata,
    TranscodeOptions transcodeOptions)
{
    // Clip-VMAF scores a centred window of this length instead of the whole file. 120s samples
    // enough motion and detail to be representative while cutting VMAF time on a long file
    // enormously — the difference between usable and unusable on low-power hosts.
    private const int ClipVmafSeconds = 120;

    // Don't bother clipping a file only a little longer than the clip; the saving is negligible.
    private const int ClipVmafMinHeadroomSeconds = 30;

    public async Task<VerificationOutcome> VerifyAsync(
        OriginalSnapshot original,
        string outputPath,
        VerificationPolicy policy,
        CancellationToken cancellationToken,
        VerificationClip? clip = null,
        IProgress<double>? qualityProgress = null,
        VmafAcceleration vmafAcceleration = VmafAcceleration.None)
    {
        var reference = clip is null
            ? original
            : await CreateReferenceClipAsync(original, clip, cancellationToken);

        try
        {
            var decodeResult = await decode.CheckAsync(outputPath, cancellationToken);
            // Packet-timestamp integrity is a video concern; skip it for an audio output.
            var timestampResult = reference.Kind == MediaKind.Audio
                ? TimestampCheckResult.NotMeasured
                : await timestamps.CheckAsync(outputPath, cancellationToken);
            var outputProbe = await probe.ProbeAsync(outputPath, cancellationToken);
            var outputSize = TryGetSize(outputPath);

            // A quick re-probe of the original (no decode) gives its audio shape so we can
            // catch a silent downmix or sample-rate drop in the output.
            var originalProbe = await probe.ProbeAsync(reference.Path, cancellationToken);

            // When the job removed tracks by language, the audio the output promised to retain
            // is the kept tracks — so channel/sample-rate expectations come from those, not from
            // a removed track (e.g. dropping a foreign 7.1 track must not excuse downmixing the
            // kept one, and must not demand 8 channels the output was never meant to have).
            var keptAudioTracks = KeptAudioTracks(originalProbe, reference.RemovedAudioStreamIndexes);

            // VMAF is expensive (a second full decode of both files), so run it only for
            // video that was actually re-encoded. Remuxes preserve the encoded frames, while
            // audio and image jobs have their own applicable verification gates.
            QualityResult? qualityResult = null;
            if (policy.RequiresVmaf(reference.Kind, reference.VideoReencoded))
            {
                // Clip-VMAF (a real full-file job, not a preview) scores a centred representative
                // window instead of the whole runtime — much faster on modest hardware, at the cost
                // of sampling only part of the file. Skip it when the source is already short.
                (int Start, int Duration)? vmafClip = null;
                if (clip is null
                    && policy.ClipVmafEnabled
                    && reference.DurationSeconds is { } total
                    && total > ClipVmafSeconds + ClipVmafMinHeadroomSeconds)
                {
                    var start = (int)Math.Max(0, (total - ClipVmafSeconds) / 2.0);
                    vmafClip = (start, ClipVmafSeconds);
                }

                qualityResult = await MeasureQualityAsync(
                    reference,
                    outputPath,
                    originalProbe,
                    quality,
                    // A preview's cheap stream-copy clip is suitable for duration/stream checks,
                    // but can retain keyframe pre-roll. VMAF decodes the full original from the
                    // exact preview start instead, keeping its frames aligned with the encode.
                    clip is null ? reference.Path : original.Path,
                    clip?.StartSeconds,
                    vmafClip?.Start,
                    vmafClip?.Duration,
                    policy.VmafFrameSubsample,
                    vmafAcceleration,
                    qualityProgress,
                    cancellationToken);
            }

            // The image SSIM gate is the still-image counterpart of VMAF: measure it only for an
            // image job when enabled (the safe default), since it runs an extra ffmpeg pass.
            var imageQualityResult = policy.ImageQualityGateEnabled && reference.Kind == MediaKind.Image
                ? await imageQuality.MeasureAsync(
                    reference.Path,
                    outputPath,
                    new ImageQualityMeasurementContext(
                        originalProbe.Width ?? 0,
                        originalProbe.Height ?? 0,
                        Optimisarr.Core.Rules.ImageSafety.MayContainAlpha(originalProbe.PixelFormat)),
                    cancellationToken)
                : null;

            // The EXIF/ICC-retention gate reads both files' metadata with exiftool; image-only and
            // enabled by default, since it spawns two extra processes.
            ImageMetadataResult? originalMetadata = null;
            ImageMetadataResult? outputMetadata = null;
            if (policy.ImageMetadataGateEnabled && reference.Kind == MediaKind.Image)
            {
                originalMetadata = await imageMetadata.ReadAsync(reference.Path, cancellationToken);
                outputMetadata = await imageMetadata.ReadAsync(outputPath, cancellationToken);
            }

            var imageMetadataMeasured = originalMetadata is { Measured: true } && outputMetadata is { Measured: true };

            // The loudness and clipping gates share one ebur128 decode of each file, so the
            // measurement runs when either is enabled; both are opt-in for the extra passes.
            LoudnessResult? originalLoudness = null;
            LoudnessResult? outputLoudness = null;
            if (policy.AudioLoudnessGateEnabled || policy.AudioClippingGateEnabled)
            {
                originalLoudness = await loudness.MeasureAsync(reference.Path, cancellationToken);
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
                OriginalSizeBytes: reference.SizeBytes,
                OutputSizeBytes: outputSize,
                OriginalDurationSeconds: reference.DurationSeconds,
                OutputDurationSeconds: outputProbe.DurationSeconds,
                OriginalAudioTrackCount: reference.AudioTrackCount,
                OutputAudioTrackCount: outputProbe.AudioTrackCount,
                OriginalSubtitleTrackCount: reference.SubtitleTrackCount,
                OutputSubtitleTrackCount: outputProbe.SubtitleTrackCount,
                OriginalIsHdr: reference.IsHdr,
                OutputIsHdr: outputProbe.IsHdr,
                HdrConvertedToSdr: reference.HdrConvertedToSdr,
                OriginalMaxAudioChannels: keptAudioTracks.Count == 0
                    ? originalProbe.MaxAudioChannels
                    : keptAudioTracks.Max(track => track.Channels),
                OutputMaxAudioChannels: outputProbe.MaxAudioChannels,
                OriginalMaxAudioSampleRate: keptAudioTracks.Count == 0
                    ? originalProbe.MaxAudioSampleRate
                    : keptAudioTracks.Max(track => track.SampleRate),
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
                OriginalVideoStartSeconds: originalProbe.VideoStartSeconds,
                OriginalAudioStartSeconds: originalProbe.AudioStartSeconds,
                OutputVideoStartSeconds: outputProbe.VideoStartSeconds,
                OutputAudioStartSeconds: outputProbe.AudioStartSeconds,
                TimestampsMeasured: timestampResult.Measured,
                NonMonotonicTimestampCount: timestampResult.NonMonotonicCount,
                TimestampRegressionDetail: timestampResult.FirstRegressionDetail,
                OutputLastPresentationSeconds: timestampResult.LastPresentationSeconds,
                Kind: reference.Kind,
                AudioReencoded: reference.AudioReencoded,
                AudioDownmixed: reference.AudioDownmixed,
                AudioTracksRemoved: reference.RemovedAudioStreamIndexes?.Count ?? 0,
                OriginalWidth: originalProbe.Width,
                OriginalHeight: originalProbe.Height,
                OutputWidth: outputProbe.Width,
                OutputHeight: outputProbe.Height,
                ImageQualityMeasured: imageQualityResult?.Measured ?? false,
                ImageQualityError: imageQualityResult?.Error,
                ImageSsim: imageQualityResult?.Ssim,
                ImageDownscaleRequested: reference.ImageDownscaleRequested,
                ImageMetadataMeasured: imageMetadataMeasured,
                ImageMetadataError: originalMetadata?.Error ?? outputMetadata?.Error,
                OriginalHasIccProfile: originalMetadata?.Metadata.HasIccProfile ?? false,
                OutputHasIccProfile: outputMetadata?.Metadata.HasIccProfile ?? false,
                OriginalHasExif: originalMetadata?.Metadata.HasExif ?? false,
                OutputHasExif: outputMetadata?.Metadata.HasExif ?? false,
                VideoReencoded: reference.VideoReencoded,
                OriginalAttachedPictureCount: originalProbe.AttachedPictureCount,
                OutputAttachedPictureCount: outputProbe.AttachedPictureCount,
                OriginalFormatTags: originalProbe.FormatTags,
                OutputFormatTags: outputProbe.FormatTags,
                OriginalVideoCodec: originalProbe.VideoCodec,
                ExpectedVideoCodec: reference.ExpectedVideoCodec,
                OriginalPixelFormat: originalProbe.PixelFormat,
                OutputPixelFormat: outputProbe.PixelFormat,
                OriginalBitsPerRawSample: originalProbe.BitsPerRawSample,
                OutputBitsPerRawSample: outputProbe.BitsPerRawSample,
                OriginalVideoProfile: originalProbe.VideoProfile,
                OutputVideoProfile: outputProbe.VideoProfile);

            return new VerificationOutcome(VerificationEvaluator.Evaluate(input, policy), outputSize);
        }
        finally
        {
            if (clip is not null)
            {
                TryDelete(reference.Path);
            }
        }
    }

    // The original's audio tracks minus the ones the job removed on purpose. Falls back to
    // every track when nothing was removed (or the probe saw no audio, e.g. an unreadable
    // original — the aggregate fields then keep their existing conservative behaviour).
    private static IReadOnlyList<AudioTrackInfo> KeptAudioTracks(
        MediaProbeResult originalProbe,
        IReadOnlyList<int>? removedIndexes)
    {
        if (removedIndexes is not { Count: > 0 })
        {
            return originalProbe.AudioTracks;
        }

        return originalProbe.AudioTracks
            .Where((_, index) => !removedIndexes.Contains(index))
            .ToList();
    }

    private static Task<QualityResult> MeasureQualityAsync(
        OriginalSnapshot reference,
        string outputPath,
        MediaProbeResult originalProbe,
        QualityScoreService quality,
        string qualityReferencePath,
        int? referenceStartSeconds,
        int? clipStartSeconds,
        int? clipDurationSeconds,
        int frameSubsample,
        VmafAcceleration acceleration,
        IProgress<double>? qualityProgress,
        CancellationToken cancellationToken)
    {
        if (originalProbe.Width is not > 0 || originalProbe.Height is not > 0)
        {
            return Task.FromResult(QualityResult.Failed(
                "Could not determine the original video dimensions required for VMAF."));
        }

        var context = new QualityMeasurementContext(
            originalProbe.Width.Value,
            originalProbe.Height.Value,
            reference.IsHdr,
            reference.HdrConvertedToSdr,
            // A clip-VMAF window seeks both inputs to its start; otherwise only the reference seek
            // (a preview) applies to the reference input.
            ReferenceStartSeconds: clipStartSeconds ?? referenceStartSeconds,
            ReferenceDurationSeconds: originalProbe.DurationSeconds,
            DistortedStartSeconds: clipStartSeconds,
            MeasureDurationSeconds: clipDurationSeconds,
            FrameSubsample: frameSubsample,
            Acceleration: acceleration);
        return quality.MeasureAsync(qualityReferencePath, outputPath, context, cancellationToken, qualityProgress);
    }

    private async Task<OriginalSnapshot> CreateReferenceClipAsync(
        OriginalSnapshot original,
        VerificationClip clip,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(clip.ReferencePath)!);
        var args = PreviewReferenceClipCommandBuilder.Build(
            original.Path,
            clip.ReferencePath,
            clip.Seconds,
            clip.StartSeconds);

        var run = await RunReferenceClipAsync(args, cancellationToken);
        if (run.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Could not create preview verification reference clip: {run.Error ?? $"ffmpeg exited with code {run.ExitCode}"}");
        }

        var clipProbe = await probe.ProbeAsync(clip.ReferencePath, cancellationToken);
        return original with
        {
            Path = clip.ReferencePath,
            SizeBytes = TryGetSize(clip.ReferencePath),
            DurationSeconds = clipProbe.DurationSeconds ?? ClipDurationFallback(original.DurationSeconds, clip.Seconds),
            AudioTrackCount = clipProbe.Success ? clipProbe.AudioTrackCount : original.AudioTrackCount,
            SubtitleTrackCount = clipProbe.Success ? clipProbe.SubtitleTrackCount : original.SubtitleTrackCount,
            IsHdr = clipProbe.Success ? clipProbe.IsHdr : original.IsHdr
        };
    }

    private async Task<(int ExitCode, string? Error)> RunReferenceClipAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = transcodeOptions.Ffmpeg,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        await stdoutTask;
        var error = await stderrTask;
        return (process.ExitCode, process.ExitCode == 0 ? null : LastLines(error, 8));
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

    private static double? ClipDurationFallback(double? originalDurationSeconds, int clipSeconds)
    {
        if (originalDurationSeconds is not > 0)
        {
            return clipSeconds;
        }

        return Math.Min(originalDurationSeconds.Value, clipSeconds);
    }

    private static string? LastLines(string? text, int maxLines)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var lines = text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .TakeLast(maxLines);
        return string.Join('\n', lines);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup; preview scratch is purged on startup and when the panel closes.
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
    }
}
