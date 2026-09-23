namespace Optimisarr.Core.Verification;

/// <summary>Escapes a file path used as an FFmpeg filter option after both parser passes.</summary>
public static class FfmpegFilterOptionPath
{
    public static string Escape(string path) =>
        path.Replace('\\', '/')
            .Replace("'", @"\\'", StringComparison.Ordinal)
            .Replace(":", @"\\:", StringComparison.Ordinal);
}
