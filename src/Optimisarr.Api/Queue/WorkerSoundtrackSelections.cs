using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Optimisarr.Api.Library;
using Optimisarr.Api.Realtime;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Library;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Verification;
using Optimisarr.Core.Workers;

namespace Optimisarr.Api.Queue;

/// <summary>
/// Chooses soundtrack bitrates for jobs a worker will encode. The server holds the original, and
/// audio-only samples are cheap, so the search runs here once and the worker receives a fixed
/// per-track command. The worker's own soundtrack gate still measures the file it produces, so a
/// worker encoder that behaves differently from this one is caught, not trusted. Like source
/// samples, a search never runs inside a claim request: the claim is answered at once and the next
/// one is offered the finished selection.
/// </summary>
public sealed class WorkerSoundtrackSelections(
    IServiceScopeFactory scopes, VerificationService verification, PerceptualResourceAdmission resources,
    ActiveEncodeRegistry encodes, IHostEnvironment environment, ILogger<WorkerSoundtrackSelections> logger,
    CancellationToken stopping)
{
    private const int MaximumRemembered = 256;
    private readonly ConcurrentDictionary<string, Task<SoundtrackSearchSummary>> _selections = new();
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private int _scratchCleared;

    public WorkerSoundtrackSelections(IServiceScopeFactory scopes, VerificationService verification,
        PerceptualResourceAdmission resources, ActiveEncodeRegistry encodes, IHostEnvironment environment,
        ILogger<WorkerSoundtrackSelections> logger, IHostApplicationLifetime lifetime)
        : this(scopes, verification, resources, encodes, environment, logger, lifetime.ApplicationStopping) { }

    /// <summary>The finished selection for this exact source and setting, or null while it is being chosen.</summary>
    public SoundtrackSearchSummary? TryTake(int jobId, string source, TranscodeSpec spec, VerificationPolicy policy)
    {
        var identity = SourceIdentity(source);
        var key = string.Join('|', jobId, identity, spec.AudioEncoder, spec.AudioBitrateKbps, spec.DownmixToStereo,
            string.Join(',', spec.RemoveAudioStreamIndexes ?? []), spec.AudioFilter, policy.MaximumSoundtrackQualityDistance,
            policy.AudioLoudnessGateEnabled, policy.MaxLoudnessDriftLufs, policy.AudioClippingGateEnabled, policy.MaxTruePeakDbtp);
        if (_selections.Count >= MaximumRemembered)
            foreach (var finished in _selections.Where(entry => entry.Value.IsCompleted).Select(entry => entry.Key).ToArray())
                _selections.TryRemove(finished, out _);
        var selection = _selections.GetOrAdd(key, _ => Task.Run(() => SelectAsync(jobId, source, spec, policy, identity), stopping));
        if (!selection.IsCompleted) return null;
        if (selection.IsCompletedSuccessfully) return selection.Result;
        // A crash in the search is not a reason to keep the job from a worker: it encodes at the
        // library bitrate, exactly as with selection off, and the next settings change retries.
        _selections.TryRemove(new KeyValuePair<string, Task<SoundtrackSearchSummary>>(key, selection));
        return Fallback(spec, policy, identity, "Soundtrack assessment failed: "
            + (selection.Exception?.GetBaseException().Message ?? "it was cancelled."));
    }

    private async Task<SoundtrackSearchSummary> SelectAsync(int jobId, string source, TranscodeSpec spec,
        VerificationPolicy policy, string identity)
    {
        await _oneAtATime.WaitAsync(stopping);
        var workRoot = WorkPaths.Resolve(environment);
        var parent = Path.Combine(workRoot, ".soundtrack-search");
        string? scratch = null;
        try
        {
            ClearAbandonedScratch(parent);
            await using var scope = scopes.CreateAsyncScope();
            var probe = await scope.ServiceProvider.GetRequiredService<IMediaProbeService>().ProbeAsync(source, stopping);
            var plan = probe is { Success: true, RawJson: { } json }
                ? SoundtrackSearchPlanner.Plan(json, spec.RemoveAudioStreamIndexes, spec.AudioEncoder, spec.AudioBitrateKbps, spec.DownmixToStereo, policy)
                : new SoundtrackSearchPlan(spec.AudioEncoder, null, [], "The source could not be probed.");
            if (plan.Unavailable is { } refusal || plan.Bitrates is null) return Fallback(spec, policy, identity, plan.Unavailable ?? "No bitrate plan.");
            using var peak = resources.ReservePeak(MediaKind.Audio, workRoot);
            if (peak is null) return Fallback(spec, policy, identity, "Not enough known memory for soundtrack assessment.");

            scratch = Path.Combine(parent, $"{jobId}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(scratch);
            var host = new SoundtrackSearchHost(scratch,
                (arguments, _, _, token) => verification.RunReferenceClipAsync(arguments, token),
                _ => { }, path => { if (File.Exists(path)) File.Delete(path); return Task.CompletedTask; },
                encodes.TrackVerification);
            var summary = await SoundtrackSearchRunner.RunAsync(source, spec, policy, plan, identity,
                scope.ServiceProvider, host, null, stopping);
            if (SourceIdentity(source) != identity)
                return Fallback(spec, policy, identity, "The source changed during soundtrack selection.");
            logger.LogInformation("Job {JobId}: chose soundtrack bitrates {Rates} for a worker", jobId,
                string.Join(", ", summary.SelectedBitrates()?.Select(rate => $"{rate} kbps") ?? []));
            return summary;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            logger.LogWarning(error, "Job {JobId}: soundtrack selection for a worker failed; the library bitrate applies", jobId);
            return Fallback(spec, policy, identity, "Soundtrack assessment failed: " + error.Message);
        }
        finally
        {
            if (scratch is not null)
                try { Directory.Delete(scratch, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            _oneAtATime.Release();
        }
    }

    // Left only by a server that stopped mid-search; nothing else writes here.
    private void ClearAbandonedScratch(string parent)
    {
        if (Interlocked.Exchange(ref _scratchCleared, 1) != 0 || !Directory.Exists(parent)) return;
        if (!LibraryPathSafety.IsSafeDirectory(parent)) return;
        foreach (var directory in Directory.EnumerateDirectories(parent))
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static SoundtrackSearchSummary Fallback(TranscodeSpec spec, VerificationPolicy policy, string identity, string reason) =>
        new(spec.AudioEncoder, spec.AudioBitrateKbps ?? AudioTarget.DefaultVideoAudioBitrateKbps,
            policy.MaximumSoundtrackQualityDistance, identity, [], reason + " Every soundtrack keeps the library bitrate.");

    // A cheap identity like the worker source sample's: this search only chooses bitrates, and the
    // worker's verification is bound to the full content hash of the file it actually encodes.
    private static string SourceIdentity(string path)
    {
        var file = new FileInfo(path);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{file.FullName}|{(file.Exists ? file.Length : -1)}|{(file.Exists ? file.LastWriteTimeUtc.Ticks : 0)}"))).ToLowerInvariant();
    }
}
