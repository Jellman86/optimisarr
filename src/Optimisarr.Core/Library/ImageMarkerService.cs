using Optimisarr.Core.Tools;

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

    /// <summary>Copies EXIF and ICC metadata from the source into the encoded image.</summary>
    public async Task<bool> CopyMetadataAsync(
        string sourcePath, string outputPath, CancellationToken cancellationToken)
    {
        var result = await RunAsync(
            ImageMetadataCommandBuilder.BuildCopyArguments(sourcePath, outputPath),
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
        var command = ExifToolCommand.Build(arguments, OperatingSystem.IsWindows());
        var result = await BoundedToolProcess.RunWithInputAsync(_exiftool, command.Arguments, command.StandardInput,
            cancellationToken, TimeSpan.FromSeconds(90));
        return result.ExitCode == 0 ? (result.ExitCode, result.Output.Trim()) : null;
    }
}
