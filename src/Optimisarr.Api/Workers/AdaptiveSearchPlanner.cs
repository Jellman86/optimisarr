using Optimisarr.Core.Queue;
using Optimisarr.Core.Verification;
using Optimisarr.Core.Workers;

namespace Optimisarr.Api.Workers;

/// <summary>
/// Turns one candidate quality into commands a worker can run: a sample encode per measurement
/// window, and the libvmaf invocation that scores each against the source.
///
/// <para>Deliberately the same shape as <see cref="RemoteQualityPlanner"/>, because it is the same
/// job done to a clip rather than a finished candidate — so a worker that can measure a delivered
/// encode can measure a sample with no new machinery.</para>
///
/// <para>Each command carries the ordinary placeholders and is run on its own, so the worker
/// substitutes its own source and its own scratch output per window. Nothing here names a path on
/// this machine.</para>
/// </summary>
internal static class AdaptiveSearchPlanner
{
    /// <inheritdoc cref="RemoteQualityPlanner"/>
    private const int WorkerThreads = 8;

    public static AdaptiveSearchStep? Plan(
        int quality,
        TranscodeSpec spec,
        string videoEncoder,
        string outputExtension,
        IReadOnlyList<VmafWindow> windows,
        VerificationPolicy policy,
        int referenceWidth,
        int referenceHeight,
        bool referenceIsHdr,
        bool hdrConvertedToSdr,
        double? sourceVideoDurationSeconds,
        double? referenceFrameRate,
        double? referenceContainerLeadSeconds,
        CropRect? crop,
        FrameRateDecimation? decimation)
    {
        // The gate being off is checked here as well as by the caller, to match
        // `RemoteQualityPlanner`. A search whose evidence will never be judged is a set of sample
        // encodes run for nothing, and the local path already declines it with "the VMAF target is
        // disabled" — the two should not disagree about whether there is anything to measure.
        if (!policy.QualityGateEnabled
            || windows.Count == 0
            || referenceWidth <= 0
            || referenceHeight <= 0)
        {
            return null;
        }

        var samples = new List<IReadOnlyList<string>>(windows.Count);
        var measurements = new List<IReadOnlyList<string>>(windows.Count);

        foreach (var window in windows)
        {
            var sampleSpec = spec with
            {
                InputPath = WorkerProtocol.InputPlaceholder,
                OutputPath = WorkerProtocol.OutputPlaceholder + outputExtension,
                Crf = quality,
                ClipStartSeconds = window.StartSeconds,
                ClipSeconds = window.DurationSeconds,
                // VMAF judges the primary picture only. Excluding other tracks keeps the measured
                // bytes a real video-size comparison, which is the number the search selects on —
                // copied audio would otherwise swamp the difference between two qualities.
                VideoOnly = true
            };

            samples.Add(FfmpegCommandBuilder.Build(
                sampleSpec,
                // The worker's cores are its own business; the server never learns how many it has.
                threads: 0,
                videoEncoder,
                optimisedMarker: null,
                // Sample windows seek into an independently encoded long-GOP source. Software
                // decode keeps decoder reordering from selecting adjacent frames and making motion
                // look like compression damage. The encoder under test is unchanged: that is the
                // whole point of running the search on the machine that will do the encode.
                hardwareDecode: false,
                hardwareToneMap: false));

            var context = new QualityMeasurementContext(
                referenceWidth,
                referenceHeight,
                referenceIsHdr,
                hdrConvertedToSdr,
                ReferenceStartSeconds: window.StartSeconds,
                ReferenceDurationSeconds: sourceVideoDurationSeconds,
                // A sample begins at its own first frame, unlike a finished candidate where the
                // window is a slice of the whole file. Seeking into it would measure the wrong
                // forty seconds against the right ones.
                DistortedStartSeconds: null,
                MeasureDurationSeconds: window.DurationSeconds,
                FrameSubsample: policy.VmafFrameSubsample,
                Acceleration: VmafAcceleration.None,
                ReferenceFrameRate: referenceFrameRate,
                ReferenceCrop: crop,
                ReferenceDecimation: decimation,
                ReferenceContainerLeadSeconds: referenceContainerLeadSeconds,
                DistortedShiftToken: RemoteQualityContract.DistortedShiftPlaceholder,
                // The distorted stream here is a 40-second clip the worker cuts and encodes, not
                // an encode of the whole title.
                DistortedIsCutClip: true);

            measurements.Add(QualityScoreCommandBuilder.Build(
                RemoteQualityContract.DistortedPlaceholder,
                RemoteQualityContract.ReferencePlaceholder,
                RemoteQualityContract.LogPlaceholder,
                context,
                WorkerThreads).Arguments);
        }

        var model = QualityScoreCommandBuilder.ModelVersionFor(
            crop?.Width ?? referenceWidth,
            crop?.Height ?? referenceHeight);

        return new AdaptiveSearchStep(
            quality,
            samples,
            new RemoteQualityContract(
                model,
                $"Adaptive sample at quality {quality}",
                policy.MinimumVmafHarmonicMean,
                policy.MinimumVmafMin,
                measurements));
    }
}
