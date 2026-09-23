using System.Diagnostics;
using Optimisarr.Core.Library;
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
            var sourceProbe = await ProbeAsync(ffprobe, source, cancellationToken);
            var candidateProbe = await ProbeAsync(ffprobe, candidate, cancellationToken);
            var decode = await new DecodeHealthCheck(ffmpeg).CheckAsync(candidate, cancellationToken);
            var sourceVideo = await timestamps.CheckAsync(source, cancellationToken);
            var candidateVideo = await timestamps.CheckAsync(candidate, cancellationToken);
            var sourceAudio = await timestamps.CheckPrimaryAudioAsync(source, cancellationToken);
            var sourceStreams = MediaProbeService.Parse(sourceProbe);
            // Confirm a short source packet scan before submitting it as evidence. The server's
            // strict mode deliberately does not repeat media reads after receiving this report.
            if (SourceTimelineAssessment.NeedsConfirmation(
                    sourceVideo.LastPresentationSeconds is { } videoEnd
                        ? Math.Max(0, videoEnd - (sourceStreams.VideoStartSeconds ?? 0)) : null,
                    sourceAudio.LastPresentationSeconds is { } audioEnd
                        ? Math.Max(0, audioEnd - (sourceStreams.AudioStartSeconds ?? 0)) : null))
            {
                var confirmed = await timestamps.CheckAsync(source, cancellationToken);
                if (confirmed.Measured && confirmed.LastPresentationSeconds is not null)
                    sourceVideo = confirmed;
            }
            return evidence with
            {
                SourceProbe = sourceProbe,
                CandidateProbe = candidateProbe,
                Decode = decode,
                SourceVideo = sourceVideo,
                CandidateVideo = candidateVideo,
                SourceAudio = sourceAudio,
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
