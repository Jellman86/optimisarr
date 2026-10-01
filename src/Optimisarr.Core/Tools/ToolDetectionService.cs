namespace Optimisarr.Core.Tools;

public sealed class ToolDetectionService(
    string? ffmpegCommand = null,
    string? vmafFfmpegCommand = null,
    string? ffprobeCommand = null,
    string? cudaVmafFfmpegCommand = null)
{
    private readonly string _ffmpeg = string.IsNullOrWhiteSpace(ffmpegCommand) ? "ffmpeg" : ffmpegCommand;
    private readonly string _vmafFfmpeg = string.IsNullOrWhiteSpace(vmafFfmpegCommand) ? "ffmpeg" : vmafFfmpegCommand;
    private readonly string _ffprobe = string.IsNullOrWhiteSpace(ffprobeCommand) ? "ffprobe" : ffprobeCommand;
    private readonly string? _cudaVmafFfmpeg = string.IsNullOrWhiteSpace(cudaVmafFfmpegCommand)
        ? null
        : cudaVmafFfmpegCommand;

    public async Task<IReadOnlyList<ToolCheckResult>> DetectAsync(CancellationToken cancellationToken)
    {
        var checks = new List<Task<ToolCheckResult>>
        {
            DetectVersionAsync("FFmpeg", _ffmpeg, required: true, cancellationToken),
            DetectVmafAsync(_vmafFfmpeg, cancellationToken),
            DetectVersionAsync("ffprobe", _ffprobe, required: true, cancellationToken)
        };
        if (_cudaVmafFfmpeg is not null)
        {
            checks.Add(DetectCudaVmafAsync(_cudaVmafFfmpeg, cancellationToken));
        }

        return await Task.WhenAll(checks);
    }

    private static async Task<ToolCheckResult> DetectCudaVmafAsync(
        string command,
        CancellationToken cancellationToken)
    {
        const string name = "FFmpeg (CUDA VMAF)";
        return await RunAsync(name, command, required: false, ["-hide_banner", "-filters"], output =>
            FfmpegFilterParser.Contains(output, "libvmaf_cuda")
                ? new ToolCheckResult(name, command, true, false, "libvmaf_cuda filter available; GPU checked at measurement time", null)
                : new ToolCheckResult(name, command, false, false, null, "libvmaf_cuda filter is not available"),
            cancellationToken);
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
        var result = await BoundedToolProcess.RunAsync(command, arguments, cancellationToken);
        return result.ExitCode == 0
            ? success(result.Output)
            : new ToolCheckResult(name, command, false, required, null,
                result.Error ?? $"Exited with code {result.ExitCode}");
    }

    private static string? FirstLine(string value)
    {
        return value
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
    }
}
