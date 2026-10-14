using System.Security.Cryptography;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Library;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Tools;
using Optimisarr.Core.Verification;

namespace Optimisarr.Core.Workers;

public sealed record PerceptualNativeTools(string Ffmpeg, string Ffprobe, string AudioMetric, string ImageMetric, string ExifTool);

internal sealed record PerceptualProbeOperations(
    Func<string, CancellationToken, Task<string>> Hash,
    Func<string, string, CancellationToken, Task<ImagePerceptualReport>> Image,
    Func<string, CancellationToken, Task<ImageMetadataResult>> Metadata,
    Func<string, CancellationToken, Task<AudioQualityInput?>> AudioInput,
    Func<IReadOnlyList<string>, string, long, CancellationToken, Task<string?>> Encode,
    Func<string, string, CancellationToken, Task<bool>> CopyMetadata,
    Func<string, string, CancellationToken, Task<AudioQualityAssessmentResult>> Audio,
    Func<string, string, ImageQualityMeasurementContext, CancellationToken, Task<ImageQualityResult>> Ssim,
    Func<string, CancellationToken, Task<LoudnessResult>> Loudness);

/// <summary>Runs one server-selected probe; all measurements remain raw and the server chooses the next setting.</summary>
public sealed class NativePerceptualProbeService
{
    private readonly PerceptualNativeTools _tools;
    private readonly PerceptualProbeOperations _operations;

    public NativePerceptualProbeService(PerceptualNativeTools tools) : this(tools, NativeOperations(tools)) { }
    internal NativePerceptualProbeService(PerceptualNativeTools tools, PerceptualProbeOperations operations)
    { _tools = tools; _operations = operations; }

    public async Task<RemotePerceptualProbeReport> MeasureAsync(RemotePerceptualSearchStep step, string source,
        string sourceHash, string scratchRoot, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        RemotePerceptualSearchPlanner.ValidateStep(step);
        var contract = step.Contract;
        var image = contract.Kind == MediaKind.Image;
        var samples = new List<RemotePerceptualSampleEvidence>();
        RemotePerceptualSourceEvidence original = new();
        string? encoderHash = null;
        RemotePerceptualProbeReport Report(string? error = null) => new(contract.Id, contract.Encoder, step.Value,
            sourceHash, original, samples.ToArray(), error, encoderHash);
        try
        {
            if (contract.RequiredSourceSha256 is { } required && !Same(required, sourceHash))
                return Report() with { Source = new(Error: "The original changed before its higher-quality retry.") };
            if (!Same(sourceHash, await _operations.Hash(source, token)))
                return Report() with { Source = new(Error: "The original changed before its sample assessment.") };
            // Required original evidence always precedes FFmpeg. In particular, ordinary still
            // decoders never see an unqualified colour profile, animation or oversized raster.
            if (image)
            {
                original = new(Image: RemoteImagePerceptualEvidence.From(
                    (await _operations.Image(source, source, token)) with { MeasurementLocation = "Worker" }));
                if (RemotePerceptualProbeValidator.SourceError(contract with
                    { Policy = contract.Policy with { ImageMetadataGateEnabled = false } }, sourceHash, original) is { } reason)
                    return Report() with { Source = original with { Error = reason } };
                if (contract.Policy.ImageMetadataGateEnabled)
                    original = original with { ImageMetadata = await _operations.Metadata(source, token) };
            }
            else original = new(Audio: await _operations.AudioInput(source, token));
            if (RemotePerceptualProbeValidator.SourceError(contract, sourceHash, original) is { } sourceError)
                return Report() with { Source = original with { Error = sourceError } };
            encoderHash = await _operations.Hash(_tools.Ffmpeg, token);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (Expected(error))
        { return Report() with { Source = original with { Error = "Required original or native tool assessment is unavailable: " + error.Message } }; }

        var tools = image ? contract.Policy.ImageMetadataGateEnabled ? new[] { _tools.Ffmpeg, _tools.ImageMetric, _tools.ExifTool }
            : [_tools.Ffmpeg, _tools.ImageMetric] : [_tools.Ffmpeg, _tools.Ffprobe, _tools.AudioMetric];
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        string? scratch = null;
        string? failure = null;
        var prepared = false;
        try
        {
            foreach (var tool in tools.Distinct(StringComparer.Ordinal)) hashes[tool] = await _operations.Hash(tool, token);
            if (!Same(encoderHash, hashes[_tools.Ffmpeg])) throw new InvalidOperationException("The encoder changed before sample preparation.");
            if (image && !Same(original.Image!.Assessment.MetricSha256, hashes[_tools.ImageMetric]))
                throw new InvalidOperationException("The bounded image metric changed after original qualification.");
            scratch = Path.Combine(Path.GetFullPath(scratchRoot), "perceptual-probe-" + Guid.NewGuid().ToString("N"));
            if (Directory.Exists(scratch) || File.Exists(scratch)) throw new IOException("The sample workspace is already occupied.");
            Directory.CreateDirectory(scratch);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(scratch, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            prepared = true;
            var extension = RemotePerceptualSearchPlanner.OutputExtension(contract.Encoder);
            for (var index = 0; index < step.SampleCommands.Count; index++)
            {
                var candidate = Path.Combine(scratch, $"candidate-{index}{extension}");
                var reference = image ? source : Path.Combine(scratch, $"reference-{index}.wav");
                if (!image)
                    await EncodeAsync(step.ReferenceCommands[index], source, reference, RemotePerceptualSearchPlanner.MaximumReferenceBytes, token);
                await EncodeAsync(step.SampleCommands[index], source, candidate, RemotePerceptualProbeValidator.MaximumSampleBytes, token);
                if (image && !await _operations.CopyMetadata(source, candidate, token) && contract.Policy.ImageMetadataGateEnabled)
                    throw new InvalidOperationException("Required image sample metadata could not be copied.");
                var bytes = Length(candidate, RemotePerceptualProbeValidator.MaximumSampleBytes);
                var referenceHash = image ? sourceHash : await _operations.Hash(reference, token);
                var candidateHash = await _operations.Hash(candidate, token);
                if (image)
                {
                    var measured = (await _operations.Image(source, candidate, token)) with { MeasurementLocation = "Worker" };
                    // An unavailable bounded comparison must never fall through to the ordinary
                    // SSIM decoder, even though the eventual final gate remains mandatory.
                    var available = PerceptualSearchEvidence.Image(measured, sourceHash, candidateHash, 0).Available;
                    var ssim = available && contract.Policy.ImageQualityGateEnabled
                        ? await _operations.Ssim(source, candidate, contract.SourceImage!, token) : null;
                    samples.Add(new(index, bytes, referenceHash, candidateHash, Image: RemoteImagePerceptualEvidence.From(measured), Ssim: ssim));
                }
                else
                {
                    var measured = await _operations.Audio(reference, candidate, token);
                    var loudness = contract.Policy.AudioLoudnessGateEnabled || contract.Policy.AudioClippingGateEnabled;
                    samples.Add(new(index, bytes, referenceHash, candidateHash, Audio: RemoteAudioQualityEvidence.From(measured),
                        SourceLoudness: loudness ? await _operations.Loudness(reference, token) : null,
                        CandidateLoudness: loudness ? await _operations.Loudness(candidate, token) : null));
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (Expected(error))
        {
            failure = "Sample measurement is unavailable: " + error.Message;
            if (!prepared) original = original with { Error = "Required native tools or sample workspace could not be prepared: " + error.Message };
        }
        finally
        {
            // Every native callback has stopped and reaped its child before returning. These
            // disposable references/candidates belong only to this unpredictable directory.
            if (scratch is not null && Directory.Exists(scratch))
            {
                try
                {
                    if (new DirectoryInfo(scratch).LinkTarget is not null)
                        original = original with { Error = "The sample workspace changed ownership; cleanup was refused." };
                    else Directory.Delete(scratch, recursive: true);
                }
                catch (Exception error) when (Expected(error))
                {
                    token.ThrowIfCancellationRequested();
                    original = original with { Error = "The owned sample workspace could not be cleaned: " + error.Message };
                }
            }
        }
        try
        {
            if (!Same(sourceHash, await _operations.Hash(source, token)))
                return Report(failure) with { Source = original with { Error = "The original changed during sample measurement." } };
            foreach (var (tool, hash) in hashes)
                if (!Same(hash, await _operations.Hash(tool, token)))
                    return Report(failure) with { Source = original with { Error = "A native tool changed during sample measurement." } };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (Expected(error))
        { return Report(failure) with { Source = original with { Error = "The original or native tools could not be revalidated: " + error.Message } }; }
        return Report(failure);
    }

    private async Task EncodeAsync(IReadOnlyList<string> portable, string source, string output, long maximum, CancellationToken token)
    {
        var suffix = Path.GetExtension(output);
        var args = portable.Select(arg => arg == WorkerProtocol.InputPlaceholder ? source
            : arg == WorkerProtocol.OutputPlaceholder + suffix || arg == RemoteQualityContract.ReferencePlaceholder ? output : arg).ToArray();
        if (await _operations.Encode(args, output, maximum, token) is { } error) throw new InvalidOperationException(error);
        Length(output, maximum);
    }

    private static long Length(string path, long maximum)
    {
        var file = new FileInfo(path);
        if (file.LinkTarget is not null || !file.Exists || file.Length <= 0 || file.Length > maximum)
            throw new IOException("The sample is missing, empty, linked or exceeds its byte budget.");
        return file.Length;
    }
    private static bool Same(string? first, string? second) => first is { Length: 64 } && second is { Length: 64 }
        && first.All(Uri.IsHexDigit) && second.All(Uri.IsHexDigit) && string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
    private static bool Expected(Exception error) => error is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception;

    private static PerceptualProbeOperations NativeOperations(PerceptualNativeTools tools) => new(
        HashAsync,
        new ImagePerceptualQualityService(tools.ImageMetric).MeasureAsync,
        new ImageMetadataService(tools.ExifTool).ReadAsync,
        async (path, token) =>
        {
            var probe = await BoundedToolProcess.RunAsync(tools.Ffprobe, AudioQualityCommandBuilder.Probe(path), token);
            return probe.ExitCode == 0 ? AudioQualityInput.Parse(probe.Output) : null;
        },
        async (args, _, _, token) =>
        {
            var result = await BoundedToolProcess.RunAsync(tools.Ffmpeg, args, token, TimeSpan.FromSeconds(90));
            return result.ExitCode == 0 ? null : result.Error ?? "The sample encode did not complete.";
        },
        new ImageMarkerService(tools.ExifTool).CopyMetadataAsync,
        (reference, candidate, token) => new AudioQualityService(tools.Ffmpeg, tools.Ffprobe, tools.AudioMetric,
            Path.GetDirectoryName(reference)!).MeasureAsync(reference, candidate, token),
        new ImageQualityService(tools.Ffmpeg).MeasureSampleAsync,
        new LoudnessService(tools.Ffmpeg).MeasureSampleAsync);

    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
    }
}
