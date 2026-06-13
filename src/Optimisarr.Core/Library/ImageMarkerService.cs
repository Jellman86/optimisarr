using System.Diagnostics;

namespace Optimisarr.Core.Library;

/// <summary>
/// Reads and writes the Optimisarr image marker via <c>exiftool</c>. ffmpeg's still encoders
/// (libwebp, mjpeg) silently drop <c>-metadata</c>, so an image's "already optimised" fingerprint
/// is instead carried in the standard EXIF/XMP <c>Software</c> field — a tool ffmpeg lacks. This
/// makes the marker portable for images: it travels with the file, surviving a database wipe or a
/// move to another machine, exactly like the container marker on video/audio.
///
/// exiftool is invoked through an explicit argument list, never a shell string. Both operations
/// are best-effort: if exiftool is missing or fails, re-optimisation is still prevented by the
/// database history and the "already in the target format" check — only marker portability is lost.
/// </summary>
public sealed class ImageMarkerService(string? exiftoolCommand = null)
{
    private readonly string _exiftool = string.IsNullOrWhiteSpace(exiftoolCommand) ? "exiftool" : exiftoolCommand;

    /// <summary>Stamps the marker into the image's Software field. Returns whether it succeeded.</summary>
    public async Task<bool> WriteAsync(string path, string markerValue, CancellationToken cancellationToken)
    {
        var software = OptimisationMarker.FormatImageSoftware(markerValue);
        var result = await RunAsync(
            new[] { "-overwrite_original", $"-Software={software}", path },
            cancellationToken);
        return result is { ExitCode: 0 };
    }

    /// <summary>Reads the marker back from the image's Software field, or <c>null</c> if absent/foreign.</summary>
    public async Task<string?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        var result = await RunAsync(new[] { "-s3", "-Software", path }, cancellationToken);
        return result is { ExitCode: 0 }
            ? OptimisationMarker.TryParseImageSoftware(result.Value.Stdout)
            : null;
    }

    private async Task<(int ExitCode, string Stdout)?> RunAsync(
        IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = _exiftool,
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
                KillQuietly(process);
                throw;
            }

            var stdout = await stdoutTask;
            await stderrTask;
            return (process.ExitCode, stdout.Trim());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // exiftool not installed or could not start — best-effort, so report failure.
            return null;
        }
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // Best effort; the process is exiting anyway.
        }
    }
}
