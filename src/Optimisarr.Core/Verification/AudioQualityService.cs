using System.Diagnostics;
using System.Security.Cryptography;
using Optimisarr.Core.Tools;

namespace Optimisarr.Core.Verification;

public sealed record AudioQualityWindowMeasurement(AudioQualityWindow Window, AudioQualityDistances Distances);

public sealed record AudioQualityAssessmentResult(
    bool Measured, string? Error, AudioQualityInput? Reference, AudioQualityInput? Candidate,
    string? ReferenceSha256, string? CandidateSha256, string? MetricSha256, string? FfmpegSha256,
    string? FfprobeSha256, IReadOnlyList<AudioQualityWindowMeasurement> Windows, double ElapsedSeconds)
{
    public string Metric => "zimtohrli";
    public string Revision => AudioQualityResultParser.Revision;
    public string Preparation => AudioQualityResultParser.Preparation;
    public bool Sampled => Reference is not null && Reference.DurationSeconds > 90;
    public double CoveredSeconds => Windows.Sum(w => w.Distances.Frames / 48000.0);
    public double? DurationDriftSeconds => Reference is null || Candidate is null ? null : Candidate.DurationSeconds - Reference.DurationSeconds;
    public double? WorstChannelDistance => Measured && Windows.Count > 0
        ? Windows.Max(w => w.Distances.WorstChannelDistance) : null;
}

/// <summary>Opt-in qualification measurements. Results never authorize replacement.</summary>
public sealed class AudioQualityService
{
    private readonly string _ffmpeg;
    private readonly string _ffprobe;
    private readonly string _metric;
    private readonly string _scratchRoot;
    private readonly Func<string, IReadOnlyList<string>, CancellationToken, TimeSpan?, Task<ToolProcessResult>> _run;

    public AudioQualityService(string ffmpeg, string ffprobe, string metric, string scratchRoot)
        : this(ffmpeg, ffprobe, metric, scratchRoot, BoundedToolProcess.RunAsync) { }

    internal AudioQualityService(string ffmpeg, string ffprobe, string metric, string scratchRoot,
        Func<string, IReadOnlyList<string>, CancellationToken, TimeSpan?, Task<ToolProcessResult>> run)
    {
        _ffmpeg = Path.GetFullPath(ffmpeg);
        _ffprobe = Path.GetFullPath(ffprobe);
        _metric = Path.GetFullPath(metric);
        _scratchRoot = Path.GetFullPath(scratchRoot);
        _run = run;
    }

    public async Task<AudioQualityAssessmentResult> MeasureAsync(string referencePath, string candidatePath, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        referencePath = Path.GetFullPath(referencePath);
        candidatePath = Path.GetFullPath(candidatePath);
        var watch = Stopwatch.StartNew();
        AudioQualityInput? reference = null, candidate = null;
        string? referenceHash = null, candidateHash = null, metricHash = null, ffmpegHash = null, ffprobeHash = null;
        var windows = new List<AudioQualityWindowMeasurement>();
        string? scratch = null;
        AudioQualityAssessmentResult Result(bool measured, string? error) => new(measured, error, reference, candidate,
            referenceHash, candidateHash, metricHash, ffmpegHash, ffprobeHash, windows.ToArray(), watch.Elapsed.TotalSeconds);
        try
        {
            referenceHash = await HashAsync(referencePath, token);
            candidateHash = await HashAsync(candidatePath, token);
            metricHash = await HashAsync(_metric, token);
            ffmpegHash = await HashAsync(_ffmpeg, token);
            ffprobeHash = await HashAsync(_ffprobe, token);
            var a = await _run(_ffprobe, ProbeArguments(referencePath), token, TimeSpan.FromSeconds(30));
            var b = await _run(_ffprobe, ProbeArguments(candidatePath), token, TimeSpan.FromSeconds(30));
            reference = a.ExitCode == 0 ? AudioQualityInput.Parse(a.Output) : null;
            candidate = b.ExitCode == 0 ? AudioQualityInput.Parse(b.Output) : null;
            if (reference is null || candidate is null)
                return Result(false, "Assessment requires one known-duration mono/stereo audio track, without video or unproved channel layouts.");
            if (AudioQualityInput.Incompatibility(reference, candidate) is { } mismatch) return Result(false, mismatch);
            // Never decode beyond the shorter track; missing duration is still exposed above.
            var plan = AudioQualityWindowPlanner.Plan(Math.Min(reference.DurationSeconds, candidate.DurationSeconds));
            Directory.CreateDirectory(_scratchRoot);
            scratch = Path.Combine(_scratchRoot, "audio-assessment-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(scratch, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            foreach (var window in plan)
            {
                token.ThrowIfCancellationRequested();
                var referencePcm = Path.Combine(scratch, "reference.raw");
                var candidatePcm = Path.Combine(scratch, "candidate.raw");
                foreach (var pair in new[] { (referencePath, referencePcm), (candidatePath, candidatePcm) })
                {
                    var decode = await _run(_ffmpeg, AudioQualityCommandBuilder.Decode(pair.Item1, pair.Item2, window),
                        token, TimeSpan.FromSeconds(90));
                    if (decode.ExitCode != 0) return Result(false, decode.Error ?? "Audio preparation failed.");
                    if (!File.Exists(pair.Item2) || new FileInfo(pair.Item2).Length is < 192000 or > 11520000)
                        return Result(false, "Prepared audio is missing, too short or exceeds the scratch budget.");
                    var bytes = new FileInfo(pair.Item2).Length;
                    var stride = reference.Channels * 4;
                    if (bytes % stride != 0 || Math.Abs(bytes / stride - window.DurationSeconds * 48000) > 64)
                        return Result(false, "Prepared audio does not cover the requested channel/frame count.");
                }
                if (new FileInfo(referencePcm).Length != new FileInfo(candidatePcm).Length)
                    return Result(false, "Prepared reference and candidate frame counts differ.");
                var measured = await _run(_metric, [referencePcm, candidatePcm, reference.Channels.ToString(System.Globalization.CultureInfo.InvariantCulture)],
                    token, TimeSpan.FromSeconds(90));
                if (measured.ExitCode != 0) return Result(false, measured.Error ?? "Audio metric failed.");
                var distances = AudioQualityResultParser.Parse(measured.Output, reference.Channels, window.DurationSeconds);
                if (distances is null) return Result(false, "Audio metric evidence is incomplete or does not match the assigned measurement.");
                windows.Add(new(window, distances));
                File.Delete(referencePcm);
                File.Delete(candidatePcm);
            }
            if (await HashAsync(referencePath, token) != referenceHash || await HashAsync(candidatePath, token) != candidateHash
                || await HashAsync(_metric, token) != metricHash || await HashAsync(_ffmpeg, token) != ffmpegHash
                || await HashAsync(_ffprobe, token) != ffprobeHash)
                return Result(false, "A media file or tool changed during assessment; the scores cannot be used.");
            token.ThrowIfCancellationRequested();
            return Result(true, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return Result(false, ex.Message); }
        finally
        {
            if (scratch is not null && Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
        }
    }

    private static IReadOnlyList<string> ProbeArguments(string path) =>
        ["-v", "error", "-show_streams", "-show_format", "-of", "json", path];

    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token));
    }
}
