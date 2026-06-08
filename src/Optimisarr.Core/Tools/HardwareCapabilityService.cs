using System.Diagnostics;

namespace Optimisarr.Core.Tools;

public sealed class HardwareCapabilityService
{
    public async Task<HardwareCapabilityResult> DetectAsync(CancellationToken cancellationToken)
    {
        var hwaccels = await RunAsync("ffmpeg", ["-hide_banner", "-hwaccels"], cancellationToken);
        var encoders = await RunAsync("ffmpeg", ["-hide_banner", "-encoders"], cancellationToken);

        var errors = new List<string>();
        if (hwaccels.ExitCode != 0)
        {
            errors.Add(hwaccels.Error ?? "ffmpeg -hwaccels failed.");
        }
        if (encoders.ExitCode != 0)
        {
            errors.Add(encoders.Error ?? "ffmpeg -encoders failed.");
        }

        return new HardwareCapabilityResult(
            hwaccels.ExitCode == 0 ? HardwareCapabilityParser.ParseHardwareAccelerators(hwaccels.Output) : [],
            encoders.ExitCode == 0 ? HardwareCapabilityParser.ParseEncoders(encoders.Output) : [],
            await CommandSucceedsAsync("nvidia-smi", ["--query-gpu=name", "--format=csv,noheader"], cancellationToken),
            Directory.Exists("/dev/dri"),
            errors.Count > 0 ? string.Join(' ', errors) : null);
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
