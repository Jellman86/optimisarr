namespace Optimisarr.Sidecar.Core.Capabilities;

/// <summary>Runs a command and reports how it went, so probing can be tested without FFmpeg.</summary>
public interface ICommandRunner
{
    Task<(int ExitCode, string Output)> RunAsync(
        string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}

/// <summary>
/// Works out what this machine can really do, in two stages: read FFmpeg's listing, then prove each
/// entry by running it.
///
/// The second stage is the point. Windows is the most varied platform Optimisarr will meet — NVENC,
/// Quick Sync and AMF may all be listed by one build while only one is usable, a driver too old for
/// the build fails on first open, and a laptop switches GPUs under you. Proving costs well under a
/// second per encoder, once per service start.
/// </summary>
public sealed class CapabilityProber(ICommandRunner runner, string platform = "windows",
    string? measurementFfmpeg = null, bool linuxDevices = false)
{
    /// <summary>Accelerators worth trying, in the order they are worth having.</summary>
    private static readonly string[] DecodeAccelerators = ["cuda", "qsv", "d3d11va", "dxva2", "vaapi"];

    public async Task<SidecarCapabilities> ProbeAsync(
        string name,
        string? ffmpeg,
        long freeScratchBytes,
        int maxConcurrency,
        CancellationToken cancellationToken = default)
    {
        if (ffmpeg is null)
        {
            return SidecarCapabilities.Nothing(name, platform);
        }

        var listing = await runner.RunAsync(ffmpeg, ["-hide_banner", "-encoders"], cancellationToken);
        if (listing.ExitCode != 0)
        {
            return SidecarCapabilities.Nothing(name, platform);
        }

        var video = new List<string>();
        foreach (var encoder in EncoderListParser.Parse(listing.Output))
        {
            var probe = await runner.RunAsync(ffmpeg, ProbeCommands.VideoEncoder(encoder, linuxDevices), cancellationToken);
            if (probe.ExitCode == 0)
            {
                video.Add(encoder);
            }
        }

        var audio = new List<string>();
        foreach (var encoder in EncoderListParser.ParseAudio(listing.Output))
        {
            var probe = await runner.RunAsync(ffmpeg, ProbeCommands.AudioEncoder(encoder), cancellationToken);
            if (probe.ExitCode == 0)
            {
                audio.Add(encoder);
            }
        }

        // No encoder means no work can be done here, whatever else is true, and reporting capacity
        // the server can never use only makes a healthy-looking worker that never takes anything.
        if (video.Count == 0 && audio.Count == 0)
        {
            return SidecarCapabilities.Nothing(name, platform) with { AudioEncoders = audio };
        }

        var decoders = video.Count > 0 ? await ProveDecodersAsync(ffmpeg, video, cancellationToken) : [];
        var vmaf = video.Count > 0 ? await ProveVmafAsync(ffmpeg, video, cancellationToken) : VmafCapability.None;

        return new SidecarCapabilities(
            name,
            OperatingSystem: platform,
            Architecture: System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            VideoEncoders: video,
            AudioEncoders: audio,
            HardwareDecoders: decoders,
            Vmaf: vmaf,
            FreeScratchBytes: Math.Max(0, freeScratchBytes),
            MaxConcurrency: Math.Max(0, maxConcurrency));
    }

    /// <summary>
    /// Each accelerator is proved by a round trip: encode a clip, then decode it back with the
    /// accelerator engaged. Listing one says FFmpeg was compiled for it, nothing more.
    /// </summary>
    private async Task<IReadOnlyList<string>> ProveDecodersAsync(
        string ffmpeg, IReadOnlyList<string> provedEncoders, CancellationToken cancellationToken)
    {
        var listing = await runner.RunAsync(ffmpeg, ["-hide_banner", "-hwaccels"], cancellationToken);
        if (listing.ExitCode != 0)
        {
            return [];
        }

        var advertised = listing.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => DecodeAccelerators.Contains(line, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (advertised.Count == 0)
        {
            return [];
        }

        // Software, so the clip under test is never itself the thing being proved.
        var encoder = provedEncoders.FirstOrDefault(e => e.StartsWith("libx", StringComparison.OrdinalIgnoreCase))
            ?? provedEncoders[0];

        var proved = new List<string>();
        var clip = Path.Combine(Path.GetTempPath(), $"optimisarr-probe-{Guid.NewGuid():N}.mp4");
        try
        {
            var made = await runner.RunAsync(
                ffmpeg, ProbeCommands.HardwareDecodeEncodeStep(clip, encoder), cancellationToken);
            if (made.ExitCode != 0)
            {
                return [];
            }

            foreach (var accelerator in advertised)
            {
                var decoded = await runner.RunAsync(
                    ffmpeg, ProbeCommands.HardwareDecodeDecodeStep(clip, accelerator, linuxDevices), cancellationToken);
                if (decoded.ExitCode == 0)
                {
                    proved.Add(accelerator);
                }
            }
        }
        finally
        {
            try { File.Delete(clip); } catch (IOException) { /* a probe clip left behind harms nothing */ }
        }

        return proved;
    }

    /// <summary>
    /// CUDA first, then CPU. A build can carry <c>libvmaf_cuda</c> while the machine has no usable
    /// CUDA device, and the server hands a worker a GPU measurement command only on the strength of
    /// this answer, so it is scored for real rather than inferred from the presence of a filter.
    /// </summary>
    private async Task<VmafCapability> ProveVmafAsync(
        string ffmpeg, IReadOnlyList<string> provedEncoders, CancellationToken cancellationToken)
    {
        var measurement = measurementFfmpeg ?? ffmpeg;
        var filters = await runner.RunAsync(measurement, ["-hide_banner", "-filters"], cancellationToken);
        if (filters.ExitCode != 0 || !filters.Output.Contains("libvmaf", StringComparison.Ordinal))
        {
            return VmafCapability.None;
        }

        var encoder = provedEncoders.FirstOrDefault(e => e.StartsWith("libx", StringComparison.OrdinalIgnoreCase))
            ?? provedEncoders[0];
        var clip = Path.Combine(Path.GetTempPath(), $"optimisarr-vmaf-{Guid.NewGuid():N}.mp4");
        try
        {
            var made = await runner.RunAsync(
                ffmpeg, ProbeCommands.HardwareDecodeEncodeStep(clip, encoder), cancellationToken);
            if (made.ExitCode != 0)
            {
                return VmafCapability.None;
            }

            foreach (var model in new[] { "vmaf_v1.0.16_3d0h", "vmaf_v1.0.16_1d5h_2160" })
            {
                var log = Path.Combine(Path.GetTempPath(), $"optimisarr-vmaf-{Guid.NewGuid():N}.json");
                try
                {
                    var measured = await runner.RunAsync(measurement, ProbeCommands.V1Vmaf(clip, model, log), cancellationToken);
                    if (measured.ExitCode != 0 || !File.Exists(log)
                        || !V1ProbeEvidence.HasFiniteFrameScores(await File.ReadAllTextAsync(log, cancellationToken)))
                        return VmafCapability.None;
                }
                finally { try { File.Delete(log); } catch (IOException) { } }
            }
            // A working legacy CUDA filter does not prove v1's complete chroma/banding features.
            return VmafCapability.Cpu;
        }
        finally
        {
            try { File.Delete(clip); } catch (IOException) { /* a probe clip left behind harms nothing */ }
        }
    }
}
