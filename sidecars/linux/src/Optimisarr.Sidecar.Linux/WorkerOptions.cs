namespace Optimisarr.Sidecar.Linux;

public sealed record WorkerOptions(string? Server, string Config, string Scratch, string Ffmpeg,
    string MeasurementFfmpeg, string Name, int Concurrency, string? Encoder)
{
    public static WorkerOptions Read(Func<string, string?> read)
    {
        var concurrencyText = read("OPTIMISARR_CONCURRENCY") ?? "1";
        if (!int.TryParse(concurrencyText, out var concurrency) || concurrency is < 1 or > 4)
            throw new ArgumentException("OPTIMISARR_CONCURRENCY must be between 1 and 4.");
        // Compose files commonly pass an unset variable through as an empty string.
        var server = read("OPTIMISARR_SERVER") is { Length: > 0 } configured ? configured : null;
        if (server is not null && !IsServerAddress(server))
            throw new ArgumentException("OPTIMISARR_SERVER must be an HTTP(S) URL without embedded credentials.");
        return new(server, read("OPTIMISARR_CONFIG_DIR") ?? "/config",
            read("OPTIMISARR_SIDECAR_WORK") ?? "/work",
            read("OPTIMISARR_FFMPEG") ?? "/usr/lib/jellyfin-ffmpeg/ffmpeg",
            read("OPTIMISARR_FFMPEG_VMAF") ?? "/usr/local/lib/optimisarr/ffmpeg-vmaf",
            read("OPTIMISARR_WORKER_NAME") ?? Environment.MachineName, concurrency,
            read("OPTIMISARR_ENCODER"));
    }

    public static bool IsServerAddress(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo);
}
