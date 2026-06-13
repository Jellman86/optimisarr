using System.Diagnostics;

namespace Optimisarr.Core.Verification;

/// <summary>The outcome of reading an image's metadata: what it carries, or why it could not be read.</summary>
public sealed record ImageMetadataResult(bool Measured, ImageMetadata Metadata, string? Error)
{
    public static ImageMetadataResult Ok(ImageMetadata metadata) => new(true, metadata, null);

    public static ImageMetadataResult Failed(string error) => new(false, default, error);
}

/// <summary>
/// Reads whether an image carries an embedded ICC colour profile and/or EXIF metadata, via
/// <c>exiftool</c> (the same tool used for the portable image marker — ffprobe does not surface
/// ICC/EXIF reliably across the still formats). The pure <see cref="ImageMetadataParser"/> turns
/// exiftool's grouped output into a presence check; this class only runs the process.
///
/// exiftool is invoked through an explicit argument list, never a shell string. Unlike the marker
/// (which is best-effort), a failure here is reported as unmeasured so the opt-in metadata gate can
/// fail closed rather than assume retention.
/// </summary>
public sealed class ImageMetadataService(string? exiftoolCommand = null)
{
    private readonly string _exiftool = string.IsNullOrWhiteSpace(exiftoolCommand) ? "exiftool" : exiftoolCommand;

    public async Task<ImageMetadataResult> ReadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return ImageMetadataResult.Failed($"File does not exist: {path}");
        }

        // -G0 prefixes each line with its top-level group ([EXIF]/[ICC_Profile]); restricting to
        // those two groups keeps the output small and unambiguous for the parser.
        var result = await RunAsync(
            new[] { "-G0", "-s", "-ICC_Profile:all", "-EXIF:all", path },
            cancellationToken);

        return result is { ExitCode: 0 }
            ? ImageMetadataResult.Ok(ImageMetadataParser.Parse(result.Value.Stdout))
            : ImageMetadataResult.Failed("exiftool could not read the image metadata (missing or failed).");
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
            return (process.ExitCode, stdout);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // exiftool not installed or could not start.
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
