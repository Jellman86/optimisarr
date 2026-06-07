using System.Diagnostics;

namespace Optimisarr.Core.Tools;

public sealed class ToolDetectionService
{
    public async Task<IReadOnlyList<ToolCheckResult>> DetectAsync(CancellationToken cancellationToken)
    {
        var checks = new[]
        {
            DetectToolAsync("FFmpeg", "ffmpeg", cancellationToken),
            DetectToolAsync("ffprobe", "ffprobe", cancellationToken)
        };

        return await Task.WhenAll(checks);
    }

    private static async Task<ToolCheckResult> DetectToolAsync(
        string name,
        string command,
        CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = command,
                ArgumentList = { "-version" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            process.Start();

            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0)
            {
                return new ToolCheckResult(name, command, false, null, FirstLine(stderr) ?? $"Exited with code {process.ExitCode}");
            }

            return new ToolCheckResult(name, command, true, FirstLine(stdout), null);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new ToolCheckResult(name, command, false, null, ex.Message);
        }
    }

    private static string? FirstLine(string value)
    {
        return value
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
    }
}
