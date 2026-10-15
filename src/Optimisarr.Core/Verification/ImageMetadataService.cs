using Optimisarr.Core.Tools;
using Optimisarr.Core.Library;

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
/// ICC/EXIF reliably across the still formats). Grouped JSON prevents values spoofing evidence;
/// missing decompression support and other tool warnings produce an unavailable measurement.
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

        // The binary ICC tag proves presence even when decoded fields are unavailable. Grouped
        // JSON prevents tag values spoofing evidence lines; warnings make the read unavailable.
        var result = await RunAsync(
            new[] { "-json", "-G0", "-s", "-ICC_Profile", "-ICC_Profile:all", "-EXIF:all", "-Warning", "-Error", path },
            cancellationToken);

        return result is { ExitCode: 0 }
            ? ImageMetadataParser.ParseJson(result.Value.Stdout)
            : ImageMetadataResult.Failed("exiftool could not read the image metadata (missing or failed).");
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
