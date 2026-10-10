using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Tools;

namespace Optimisarr.Core.Verification;

public sealed record ImagePerceptualMeasurement(int Width, int Height, bool Alpha, double MaximumAlphaError,
    double Score, IReadOnlyList<double> BackgroundScores);

public static class ImagePerceptualResultParser
{
    public const string Revision = "a7a9c787341cf703dede03c2009fa460cae5e5df";
    public const string Preparation = "sdr-srgb-native-still-v1";

    public static ImagePerceptualMeasurement? Parse(string json)
    {
        if (json.Length > 8192) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() != 1)
                || root.GetProperty("schema").GetInt32() != 1
                || root.GetProperty("metric").GetString() != "ssimulacra2"
                || root.GetProperty("revision").GetString() != Revision
                || root.GetProperty("preparation").GetString() != Preparation) return null;
            var result = new ImagePerceptualMeasurement(root.GetProperty("width").GetInt32(), root.GetProperty("height").GetInt32(),
                root.GetProperty("alpha").GetBoolean(), root.GetProperty("maximumAlphaError").GetDouble(),
                root.GetProperty("score").GetDouble(), root.GetProperty("backgroundScores").EnumerateArray().Select(v => v.GetDouble()).ToArray());
            return Valid(result) ? result : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        {
            return null;
        }
    }

    public static bool Valid(ImagePerceptualMeasurement? value) => value is { Width: >= 8 and <= 16384, Height: >= 8 and <= 16384 }
        && (long)value.Width * value.Height <= 16000000
        && double.IsFinite(value.MaximumAlphaError) && value.MaximumAlphaError is >= 0 and <= 1
        && (value.Alpha || value.MaximumAlphaError == 0)
        && double.IsFinite(value.Score) && value.Score <= 100
        && value.BackgroundScores is { } scores && scores.Count == (value.Alpha ? 2 : 1)
        && scores.All(score => double.IsFinite(score) && score <= 100)
        && Math.Abs(value.Score - scores.Min()) <= 0.000001;
}

public sealed record ImagePerceptualReport(ImagePerceptualMeasurement? Measurement, string? Error,
    string? SourceSha256, string? CandidateSha256, string? MetricSha256, double ElapsedSeconds,
    bool GateEnabled = false, double? MinimumScore = null, bool? GatePassed = null,
    string MeasurementLocation = "Server")
{
    public string Metric => "ssimulacra2";
    public string Revision => ImagePerceptualResultParser.Revision;
    public string Preparation => ImagePerceptualResultParser.Preparation;
}

/// <summary>Direct native decoding preserves ICC and alpha; no intermediate image or runtime download.</summary>
public sealed class ImagePerceptualQualityService
{
    private readonly string _metric;
    private readonly Func<string, IReadOnlyList<string>, CancellationToken, TimeSpan?, Task<ToolProcessResult>> _run;
    private readonly Func<string, CancellationToken, Task<string>> _hash;
    private readonly SemaphoreSlim _lane = new(1, 1);

    public ImagePerceptualQualityService(string metric) : this(metric, BoundedToolProcess.RunAsync) { }

    internal ImagePerceptualQualityService(string metric,
        Func<string, IReadOnlyList<string>, CancellationToken, TimeSpan?, Task<ToolProcessResult>> run,
        Func<string, CancellationToken, Task<string>>? hash = null)
    {
        _metric = Path.GetFullPath(metric);
        _run = run;
        _hash = hash ?? HashAsync;
    }

    public async Task<ImagePerceptualReport> MeasureAsync(string source, string candidate, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // A full-resolution metric has a substantial working set. Share one lane
        // across this server's image jobs rather than multiplying it by queue size.
        await _lane.WaitAsync(token);
        var watch = Stopwatch.StartNew();
        string? originalHash = null, candidateHash = null, metricHash = null;
        ImagePerceptualReport Unavailable(string reason) => new(null, reason, originalHash, candidateHash, metricHash, watch.Elapsed.TotalSeconds);
        try
        {
            source = Path.GetFullPath(source);
            candidate = Path.GetFullPath(candidate);
            originalHash = await _hash(source, token);
            candidateHash = await _hash(candidate, token);
            metricHash = await _hash(_metric, token);
            var result = await _run(_metric, [source, candidate], token, TimeSpan.FromSeconds(90));
            if (result.ExitCode != 0) return Unavailable(result.Error ?? "The native image metric failed.");
            var measurement = ImagePerceptualResultParser.Parse(result.Output);
            if (measurement is null) return Unavailable("The image metric returned unsupported or incomplete evidence.");
            if (originalHash != await _hash(source, token) || candidateHash != await _hash(candidate, token)
                || metricHash != await _hash(_metric, token))
                return Unavailable("The source, candidate or metric changed during image assessment.");
            return new(measurement, null, originalHash, candidateHash, metricHash, watch.Elapsed.TotalSeconds);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return Unavailable("Image quality could not be measured. Check tool availability and file access.");
        }
        finally { _lane.Release(); }
    }

    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        if (stream.Length > 128L * 1024 * 1024) throw new IOException("Image assessment file exceeds its read budget.");
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
    }
}

public sealed class ImagePerceptualObservationService(Func<string, string, CancellationToken, Task<ImagePerceptualReport>>? measure)
{
    public async Task<ImagePerceptualReport?> ObserveAsync(bool enabled, MediaKind kind, bool healthy, bool preview,
        bool remote, string source, string candidate, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!enabled || kind != MediaKind.Image || preview) return null;
        if (remote) return Unavailable("This worker protocol does not support image assessment.") with { MeasurementLocation = "Worker" };
        if (!healthy) return Unavailable("Skipped because the candidate failed decode health.");
        if (measure is null) return Unavailable("The native image assessment tool is unavailable on this host.");
        return await measure(source, candidate, token);
    }

    private static ImagePerceptualReport Unavailable(string reason) => new(null, reason, null, null, null, 0);
}

public static class ImagePerceptualQualityGate
{
    public const string CheckName = "Perceptual image quality (SSIMULACRA2)";
    public static bool ValidLimit(double? value) => value is >= 0 and <= 100 && double.IsFinite(value.Value);

    public static VerificationReport Apply(VerificationReport report, MediaKind kind, VerificationPolicy policy, bool preview = false)
    {
        if (preview || kind != MediaKind.Image || !policy.ImagePerceptualGateEnabled) return report;
        var image = report.ImagePerceptualQuality;
        static bool Hash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
        var reason = !ValidLimit(policy.MinimumImagePerceptualScore)
            ? "Set an explicit minimum SSIMULACRA2 score between 0 and 100."
            : image?.Error ?? (!ImagePerceptualResultParser.Valid(image?.Measurement)
                || !Hash(image?.SourceSha256) || !Hash(image?.CandidateSha256) || !Hash(image?.MetricSha256)
                ? "Complete image quality evidence is unavailable." : null);
        if (reason is null && image!.Measurement!.MaximumAlphaError > 0.000001)
            reason = "The candidate's transparency differs from the source.";
        var passed = reason is null && image!.Measurement!.Score >= policy.MinimumImagePerceptualScore!.Value;
        var detail = reason ?? string.Create(CultureInfo.InvariantCulture,
            $"SSIMULACRA2 score {image!.Measurement!.Score:G}; minimum allowed {policy.MinimumImagePerceptualScore:G}.");
        if (!passed) detail += " Replacement is blocked. The original is unchanged.";
        return report with
        {
            Checks = [.. report.Checks, new(CheckName, passed ? CheckOutcome.Passed : CheckOutcome.Failed, detail)],
            ImagePerceptualQuality = image is null ? null : image with
                { GateEnabled = true, MinimumScore = ValidLimit(policy.MinimumImagePerceptualScore) ? policy.MinimumImagePerceptualScore : null, GatePassed = passed }
        };
    }
}
