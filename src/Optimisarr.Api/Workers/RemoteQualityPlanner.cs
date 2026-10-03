using Optimisarr.Core.Queue;
using Optimisarr.Core.Verification;
using Optimisarr.Core.Workers;

namespace Optimisarr.Api.Workers;

/// <summary>
/// Builds the measurement a worker is asked to make, from the same facts local verification
/// would use: the windows planned from the source duration, and the libvmaf command built by
/// <see cref="QualityScoreCommandBuilder"/> for each. The worker's hardware is never asked to
/// accelerate the comparison, so the frames it judges are the ones software decode yields.
/// </summary>
internal static class RemoteQualityPlanner
{
    /// <summary>
    /// libvmaf's thread count is part of the command and the server never learns a worker's core
    /// count. Eight matches the smallest Apple Silicon a worker is likely to be; libvmaf caps at
    /// the cores it finds, so a smaller machine is not oversubscribed.
    /// </summary>
    private const int WorkerThreads = 8;

    public static RemoteQualityContract? Plan(
        VerificationPolicy policy,
        int referenceWidth,
        int referenceHeight,
        bool referenceIsHdr,
        bool hdrConvertedToSdr,
        double? durationSeconds,
        double? referenceFrameRate,
        double? referenceContainerLeadSeconds,
        CropRect? crop,
        FrameRateDecimation? decimation,
        string? referenceVideoCodec = null,
        string? modelVersion = null)
    {
        if (!policy.QualityGateEnabled || referenceWidth <= 0 || referenceHeight <= 0)
        {
            return null;
        }

        var windows = durationSeconds is { } total && total > 0
            ? VmafWindowPlanner.Plan(total, policy.ClipVmafEnabled)
            : [VmafWindow.Full];
        var sampling = windows.Count == 1 && windows[0] == VmafWindow.Full
            ? "Full file"
            : "Three 40-second samples (early, middle and late)";

        // Workers select this alternative only after proving equal decoded counts. Full-file
        // comparisons need it too: repeated timestamps can otherwise pair neighbouring pictures.
        var pairable = referenceFrameRate is > 0 && double.IsFinite(referenceFrameRate.Value) && decimation is null;
        var commands = new List<IReadOnlyList<string>>(windows.Count);
        var framePaired = pairable ? new List<IReadOnlyList<string>>(windows.Count) : null;
        foreach (var window in windows)
        {
            var context = new QualityMeasurementContext(
                referenceWidth,
                referenceHeight,
                referenceIsHdr,
                hdrConvertedToSdr,
                ReferenceStartSeconds: window.StartSeconds,
                ReferenceDurationSeconds: durationSeconds,
                DistortedStartSeconds: window.StartSeconds,
                MeasureDurationSeconds: window.DurationSeconds,
                FrameSubsample: policy.VmafFrameSubsample,
                Acceleration: VmafAcceleration.None,
                ReferenceFrameRate: referenceFrameRate,
                ReferenceCrop: crop,
                ReferenceDecimation: decimation,
                ReferenceContainerLeadSeconds: referenceContainerLeadSeconds,
                DistortedShiftToken: RemoteQualityContract.DistortedShiftPlaceholder,
                ReferenceVideoCodec: referenceVideoCodec,
                EncodedVideoFromWorker: true,
                ModelVersion: modelVersion);
            var command = QualityScoreCommandBuilder.Build(
                RemoteQualityContract.DistortedPlaceholder,
                RemoteQualityContract.ReferencePlaceholder,
                RemoteQualityContract.LogPlaceholder,
                context,
                WorkerThreads);
            commands.Add(command.Arguments);
            framePaired?.Add(QualityScoreCommandBuilder.Build(
                RemoteQualityContract.DistortedPlaceholder,
                RemoteQualityContract.ReferencePlaceholder,
                RemoteQualityContract.LogPlaceholder,
                context with { PairFramesByNumber = true },
                WorkerThreads).Arguments);
        }

        var model = modelVersion ?? QualityScoreCommandBuilder.ModelVersionFor(
            crop?.Width ?? referenceWidth,
            crop?.Height ?? referenceHeight, referenceIsHdr, referenceFrameRate, decimation is not null);
        return new RemoteQualityContract(
            model,
            sampling,
            policy.MinimumVmafHarmonicMean,
            policy.MinimumVmafMin,
            commands,
            FramePairedCommands: framePaired);
    }
}
