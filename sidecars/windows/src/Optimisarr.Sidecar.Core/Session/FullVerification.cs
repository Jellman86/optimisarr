using System.Diagnostics;
using Optimisarr.Core.Verification;
using Optimisarr.Core.Workers;

namespace Optimisarr.Sidecar.Core.Session;

/// <summary>All media reads stay on this worker. The server evaluates the returned measurements.</summary>
public static class FullVerification
{
    public static async Task<RemoteVerificationEvidence> MeasureAsync(
        string ffmpeg, string source, string candidate, RemoteVerificationContract contract,
        string sourceHash, string candidateHash, CancellationToken cancellationToken)
    {
        var evidence = new RemoteVerificationEvidence(contract.Id, sourceHash, candidateHash);
        try
        {
            if (contract.Version != 1) throw new InvalidOperationException("Unsupported full verification contract.");
            var ffprobe = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(ffmpeg))!,
                OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
            var timestamps = new TimestampIntegrityCheck(ffprobe);
            var loudness = new LoudnessService(ffmpeg);
            return evidence with
            {
                SourceProbe = await ProbeAsync(ffprobe, source, cancellationToken),
                CandidateProbe = await ProbeAsync(ffprobe, candidate, cancellationToken),
                Decode = await new DecodeHealthCheck(ffmpeg).CheckAsync(candidate, cancellationToken),
                SourceVideo = await timestamps.CheckAsync(source, cancellationToken),
                CandidateVideo = await timestamps.CheckAsync(candidate, cancellationToken),
                SourceAudio = await timestamps.CheckPrimaryAudioAsync(source, cancellationToken),
                SourceLoudness = contract.MeasureAudio ? await loudness.MeasureAsync(source, cancellationToken) : null,
                CandidateLoudness = contract.MeasureAudio ? await loudness.MeasureAsync(candidate, cancellationToken) : null
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return evidence with { Error = "Full verification could not complete: " + ex.Message };
        }
    }

    private static async Task<string> ProbeAsync(string ffprobe, string path, CancellationToken token)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(ffprobe)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                ArgumentList = { "-v", "error", "-print_format", "json", "-show_format", "-show_streams", path }
            }
        };
        process.Start();
        using var stop = token.Register(() => { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        var output = ReadBoundedAsync(process.StandardOutput, token);
        var error = ReadBoundedAsync(process.StandardError, token);
        // Readers and wait are observed together; an oversized probe cannot leave a child blocked
        // on an unread pipe. A reader failure kills its writer before it propagates.
        try
        {
            await Task.WhenAll(output, error, process.WaitForExitAsync(token));
            if (process.ExitCode != 0) throw new InvalidOperationException("ffprobe failed: " + await error);
            return await output;
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }

        async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellation)
        {
            var text = new System.Text.StringBuilder();
            var buffer = new char[8192];
            try
            {
                int count;
                while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellation)) > 0)
                {
                    if (text.Length + count > 1024 * 1024)
                        throw new InvalidOperationException("ffprobe evidence exceeded the 1 MiB limit.");
                    text.Append(buffer, 0, count);
                }
                return text.ToString();
            }
            catch { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } throw; }
        }
    }
}
