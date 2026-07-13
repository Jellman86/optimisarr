namespace Optimisarr.Core.Tools;

/// <summary>
/// Resolves the paired FFmpeg/ffprobe commands used by production media work. Keeping this
/// policy in one place prevents a configured FFmpeg build from transcoding a file that an
/// unrelated ffprobe installation then interprets differently.
/// </summary>
public static class MediaToolCommands
{
    public static string ResolveFfmpeg(string? configuredCommand) =>
        string.IsNullOrWhiteSpace(configuredCommand) ? "ffmpeg" : configuredCommand.Trim();

    public static string ResolveFfprobe(string? configuredCommand, string? ffmpegCommand)
    {
        if (!string.IsNullOrWhiteSpace(configuredCommand))
        {
            return configuredCommand.Trim();
        }

        var ffmpeg = ResolveFfmpeg(ffmpegCommand);
        var directory = Path.GetDirectoryName(ffmpeg);
        if (string.IsNullOrEmpty(directory))
        {
            return "ffprobe";
        }

        var extension = Path.GetExtension(ffmpeg);
        return Path.Combine(directory, $"ffprobe{extension}");
    }
}
