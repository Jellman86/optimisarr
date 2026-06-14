using System.Diagnostics;

namespace Optimisarr.Core.Tools;

public sealed class HardwareCapabilityService(string? ffmpegCommand = null)
{
    // Detection must use the same ffmpeg as the transcode path, or the reported encoder list
    // can differ from what actually runs (see OPTIMISARR_FFMPEG in Program.cs).
    private readonly string _ffmpeg = string.IsNullOrWhiteSpace(ffmpegCommand) ? "ffmpeg" : ffmpegCommand;

    // Detection runs ffmpeg several times (and a test encode per hardware encoder), so the result
    // is cached: hardware does not change while the process runs. The Tools page can force a fresh
    // probe (e.g. after the operator adds a GPU or fixes a driver); the per-job path uses the cache.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HardwareCapabilityResult? _cached;

    public async Task<HardwareCapabilityResult> DetectAsync(
        CancellationToken cancellationToken, bool forceRefresh = false)
    {
        if (!forceRefresh && _cached is { } hit)
        {
            return hit;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!forceRefresh && _cached is { } cached)
            {
                return cached;
            }

            var result = await DetectCoreAsync(cancellationToken);
            _cached = result;
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<HardwareCapabilityResult> DetectCoreAsync(CancellationToken cancellationToken)
    {
        var hwaccels = await RunAsync(_ffmpeg, ["-hide_banner", "-hwaccels"], cancellationToken);
        var encoders = await RunAsync(_ffmpeg, ["-hide_banner", "-encoders"], cancellationToken);

        var errors = new List<string>();
        if (hwaccels.ExitCode != 0)
        {
            errors.Add(hwaccels.Error ?? "ffmpeg -hwaccels failed.");
        }
        if (encoders.ExitCode != 0)
        {
            errors.Add(encoders.Error ?? "ffmpeg -encoders failed.");
        }

        var nvidiaRuntimeAvailable =
            await CommandSucceedsAsync("nvidia-smi", ["--query-gpu=name", "--format=csv,noheader"], cancellationToken);
        var driDeviceAvailable = Directory.Exists("/dev/dri");

        // The parser gives a cheap first pass (listed + the device/runtime pre-filter). A hardware
        // encoder that clears that gate is then confirmed with a real test encode, so a present-but-
        // broken driver or an unsupported codec on this GPU is reported unavailable rather than
        // assumed working. CPU encoders are trusted from the listing.
        var parsed = encoders.ExitCode == 0
            ? HardwareCapabilityParser.ParseEncoders(encoders.Output, nvidiaRuntimeAvailable, driDeviceAvailable)
            : [];
        var confirmed = await ConfirmEncodersAsync(parsed, cancellationToken);

        return new HardwareCapabilityResult(
            hwaccels.ExitCode == 0 ? HardwareCapabilityParser.ParseHardwareAccelerators(hwaccels.Output) : [],
            confirmed,
            nvidiaRuntimeAvailable,
            driDeviceAvailable,
            errors.Count > 0 ? string.Join(' ', errors) : null);
    }

    private async Task<IReadOnlyList<EncoderCapability>> ConfirmEncodersAsync(
        IReadOnlyList<EncoderCapability> encoders, CancellationToken cancellationToken)
    {
        var confirmed = new List<EncoderCapability>(encoders.Count);
        foreach (var encoder in encoders)
        {
            // CPU encoders are trusted; a hardware encoder the pre-filter already ruled out (no
            // device/runtime) stays unavailable without paying for a probe that would only fail.
            if (encoder.Mode == "CPU" || !encoder.Available)
            {
                confirmed.Add(encoder);
                continue;
            }

            var probe = await RunAsync(_ffmpeg, EncoderProbeCommand.Build(encoder.Name), cancellationToken);
            confirmed.Add(encoder with { Available = probe.ExitCode == 0 });
        }

        return confirmed;
    }

    private static async Task<CommandResult> RunAsync(string command, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = command,
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
            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            return new CommandResult(process.ExitCode, stdout, FirstLine(stderr));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new CommandResult(-1, string.Empty, ex.Message);
        }
    }

    private static async Task<bool> CommandSucceedsAsync(string command, IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
        (await RunAsync(command, arguments, cancellationToken)).ExitCode == 0;

    private static string? FirstLine(string value) =>
        value.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

    private sealed record CommandResult(int ExitCode, string Output, string? Error);
}
