using System.Diagnostics;

namespace Optimisarr.Core.Tools;

public sealed class ToolDetectionService(
    string? ffmpegCommand = null,
    string? vmafFfmpegCommand = null,
    string? ffprobeCommand = null)
{
    private readonly string _ffmpeg = string.IsNullOrWhiteSpace(ffmpegCommand) ? "ffmpeg" : ffmpegCommand;
    private readonly string _vmafFfmpeg = string.IsNullOrWhiteSpace(vmafFfmpegCommand) ? "ffmpeg" : vmafFfmpegCommand;
    private readonly string _ffprobe = string.IsNullOrWhiteSpace(ffprobeCommand) ? "ffprobe" : ffprobeCommand;

    public async Task<IReadOnlyList<ToolCheckResult>> DetectAsync(CancellationToken cancellationToken)
    {
        var checks = new[]
        {
            DetectVersionAsync("FFmpeg", _ffmpeg, required: true, cancellationToken),
            DetectVmafAsync(_vmafFfmpeg, cancellationToken),
            DetectVersionAsync("ffprobe", _ffprobe, required: true, cancellationToken)
        };

        return await Task.WhenAll(checks);
    }

    private static async Task<ToolCheckResult> DetectVersionAsync(
        string name,
        string command,
        bool required,
        CancellationToken cancellationToken)
    {
        return await RunAsync(name, command, required, ["-version"], output =>
            new ToolCheckResult(name, command, true, required, FirstLine(output), null), cancellationToken);
    }

    private static async Task<ToolCheckResult> DetectVmafAsync(
        string command,
        CancellationToken cancellationToken)
    {
        const string name = "FFmpeg (VMAF)";
        return await RunAsync(name, command, required: false, ["-hide_banner", "-filters"], output =>
            FfmpegFilterParser.Contains(output, "libvmaf")
                ? new ToolCheckResult(name, command, true, false, "libvmaf filter available", null)
                : new ToolCheckResult(name, command, false, false, null, "libvmaf filter is not available"),
            cancellationToken);
    }

    private static async Task<ToolCheckResult> RunAsync(
        string name,
        string command,
        bool required,
        IReadOnlyList<string> arguments,
        Func<string, ToolCheckResult> success,
        CancellationToken cancellationToken)
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

            if (process.ExitCode != 0)
            {
                return new ToolCheckResult(name, command, false, required, null, FirstLine(stderr) ?? $"Exited with code {process.ExitCode}");
            }

            return success(stdout);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new ToolCheckResult(name, command, false, required, null, ex.Message);
        }
    }

    private static string? FirstLine(string value)
    {
        return value
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
    }
}
