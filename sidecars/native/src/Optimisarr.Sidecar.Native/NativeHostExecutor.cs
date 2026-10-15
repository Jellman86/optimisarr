using System.Text.Json;
using Optimisarr.Sidecar.Core.Capabilities;
using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Native;

internal static class NativeHostExecutor
{
    public static async Task<NativeHostEvent> RunAsync(NativeHostRequest request, Action<NativeHostEvent> report, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (request.Mode == "recover")
            return new("recovered", Recovery: NativeScratchWorkspace.Recover(request.ScratchRoot,
                afterOwner: request.RecoveryAfterOwner, cancellationToken: token));
        if (request.Mode == "requirements")
        {
            var planned = request.Assignment!.Value.Deserialize<Assignment>(NativeHostProtocol.Json)!;
            return new("requirements", planned.JobId,
                NativeMemoryBytes: Optimisarr.Core.Workers.PerceptualResourceBudget.MemoryBytes(planned.Kind, planned.RequiredScratchBytes!.Value, false),
                RequiredScratchBytes: planned.RequiredScratchBytes);
        }
        Environment.SetEnvironmentVariable("OPTIMISARR_IMAGE_QUALITY", request.ImageMetric);
        Environment.SetEnvironmentVariable("OPTIMISARR_AUDIO_QUALITY", request.AudioMetric);
        Environment.SetEnvironmentVariable("OPTIMISARR_EXIFTOOL", request.MetadataTool);
        if (request.Mode == "capabilities")
        {
            var proof = await PerceptualCapabilityProber.ForFfmpeg(request.FfmpegPath).ProbeAsync(request.ScratchRoot, token);
            return new("capabilities", Capabilities: proof);
        }
        var assignment = request.Assignment!.Value.Deserialize<Assignment>(NativeHostProtocol.Json)!;
        using var control = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        using var transfers = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var client = new SidecarClient(control);
        var runtime = PerceptualJobRuntime.Native(request.FfmpegPath, freeMemory: () => request.GrantedMemoryBytes);
        var runner = new JobRunner(client, new JobTransfer(transfers, client.Diagnostics), new ProcessTranscoder(),
            request.FfmpegPath, request.ScratchRoot, () => null,
            report: detail => report(new("status", assignment.JobId, Detail: detail)),
            observe: job => report(new("progress", job.JobId, job.Stage.ToString(), job.EncodedSeconds)), perceptual: runtime);
        var outcome = await runner.RunAsync(request.Pairing!, assignment, token);
        return new("completed", assignment.JobId, Outcome: outcome);
    }
}
