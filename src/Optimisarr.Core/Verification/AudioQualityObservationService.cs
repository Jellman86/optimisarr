using Optimisarr.Core.Domain;
using Optimisarr.Core.Workers;

namespace Optimisarr.Core.Verification;

/// <summary>Measurements remain separate from gate decisions; remote work never falls back to server reads.</summary>
public sealed class AudioQualityObservationService(
    Func<string, string, CancellationToken, Task<AudioQualityAssessmentResult>>? measure)
{
    public async Task<AudioQualityReport?> ObserveAsync(bool enabled, MediaKind kind, bool healthy, bool clip,
        string source, string candidate, RemoteVerificationEvidence? remote, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!enabled || kind != MediaKind.Audio || clip) return null;
        var location = remote is null ? "Server" : "Worker";
        if (!healthy) return new(location, null, "Skipped because the candidate failed decode health.");
        if (remote is not null)
        {
            var reason = AudioQualityReporting.Validate(remote.AudioQuality, remote.SourceSha256, remote.CandidateSha256);
            return new(location, reason is null ? remote.AudioQuality : null, reason);
        }
        if (measure is null) return new(location, null, "The audio assessment tool is unavailable on this host.");
        try
        {
            var result = await measure(source, candidate, token);
            var evidence = RemoteAudioQualityEvidence.From(result);
            var error = AudioQualityReporting.Validate(evidence, result.ReferenceSha256, result.CandidateSha256);
            return new(location, error is null ? evidence : null, error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            return new(location, null, "Audio quality could not be measured: " + ex.Message);
        }
    }
}

public static class AudioQualityTools
{
    public static AudioQualityService? Create(string ffmpeg, string ffprobe, string? metric, string scratch)
    {
        var encoder = Find(ffmpeg);
        var probe = Find(ffprobe);
        var assessment = Find(metric ?? Path.Combine(AppContext.BaseDirectory,
            OperatingSystem.IsWindows() ? "optimisarr-audio-quality.exe" : "optimisarr-audio-quality"));
        return encoder is null || probe is null || assessment is null
            ? null : new(encoder, probe, assessment, scratch);
    }

    private static string? Find(string path)
    {
        if (Path.IsPathFullyQualified(path)) return File.Exists(path) ? path : null;
        if (path.Contains(Path.DirectorySeparatorChar) || path.Contains(Path.AltDirectorySeparatorChar))
            return File.Exists(path) ? Path.GetFullPath(path) : null;
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            var candidate = Path.Combine(directory, path);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            if (OperatingSystem.IsWindows() && File.Exists(candidate + ".exe")) return Path.GetFullPath(candidate + ".exe");
        }
        return null;
    }
}
