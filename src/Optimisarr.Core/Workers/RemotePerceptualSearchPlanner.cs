using System.Globalization;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Queue;

namespace Optimisarr.Core.Workers;

public sealed record RemotePerceptualSearchStep(RemotePerceptualSearchContract Contract, int Value, int CandidateIndex,
    IReadOnlyList<IReadOnlyList<string>> SampleCommands, IReadOnlyList<IReadOnlyList<string>> ReferenceCommands);

/// <summary>Portable commands for a frozen search, without leaking server paths or changing encoders.</summary>
public static class RemotePerceptualSearchPlanner
{
    public const long MaximumReferenceBytes = 16L * 1024 * 1024;

    public static RemotePerceptualSearchStep Plan(RemotePerceptualSearchContract contract, int value, int candidateIndex, TranscodeSpec source)
    {
        if (!RemotePerceptualProbeValidator.ValidContract(contract) || !contract.Plan.Values.Contains(value)
            || candidateIndex is < 0 or >= PerceptualQualitySearch.MaximumProbes
            || source.Kind != contract.Kind || source.DownmixToStereo || source.ImageScaleFilter is not null || source.ImageLossless
            || source.ClipStartSeconds is not null || source.ClipSeconds is not null
            || (contract.Kind == MediaKind.Image ? source.ImageEncoder : source.AudioEncoder) != contract.Encoder)
            throw new ArgumentException("A sample step requires a supported frozen quality-first contract and its original encoder intent.");
        var extension = OutputExtension(contract.Encoder);
        var portable = source with { InputPath = WorkerProtocol.InputPlaceholder, OutputPath = WorkerProtocol.OutputPlaceholder + extension };
        var samples = new List<IReadOnlyList<string>>();
        var references = new List<IReadOnlyList<string>>();
        if (contract.Kind == MediaKind.Image)
            samples.Add(Bounded(FfmpegCommandBuilder.Build(portable with { ImageQuality = value }), RemotePerceptualProbeValidator.MaximumSampleBytes));
        else
        {
            foreach (var window in contract.AudioWindows)
            {
                samples.Add(Bounded(FfmpegCommandBuilder.Build(PerceptualSamplePlanner.AudioCandidate(portable,
                    portable.OutputPath, value, window)), RemotePerceptualProbeValidator.MaximumSampleBytes));
                references.Add(Bounded(PerceptualSamplePlanner.AudioReference(WorkerProtocol.InputPlaceholder,
                    RemoteQualityContract.ReferencePlaceholder, window), MaximumReferenceBytes));
            }
        }
        return new(contract, value, candidateIndex, samples.ToArray(), references.ToArray());
    }

    public static string OutputExtension(string encoder) => encoder switch
    {
        "mjpeg" => ".jpg", "libwebp" => ".webp", "aac" => ".m4a", "libopus" => ".opus", "libmp3lame" => ".mp3",
        _ => throw new ArgumentException("Unsupported perceptual encoder.")
    };

    public static void ValidateStep(RemotePerceptualSearchStep step)
    {
        if (step?.Contract is not { } contract) throw new ArgumentException("A probe needs a frozen search contract.");
        var image = contract.Kind == MediaKind.Image;
        var spec = new TranscodeSpec(WorkerProtocol.InputPlaceholder, WorkerProtocol.OutputPlaceholder + OutputExtension(contract.Encoder),
            null, null, null, false, Kind: contract.Kind, AudioEncoder: image ? null : contract.Encoder,
            AudioBitrateKbps: image ? null : step.Value, ImageEncoder: image ? contract.Encoder : null, ImageQuality: image ? step.Value : null);
        var canonical = Plan(contract, step.Value, step.CandidateIndex, spec);
        if (!CommandsEqual(canonical.SampleCommands, step.SampleCommands) || !CommandsEqual(canonical.ReferenceCommands, step.ReferenceCommands))
            throw new ArgumentException("The sample commands are outside the frozen original-source contract.");
    }

    private static bool CommandsEqual(IReadOnlyList<IReadOnlyList<string>> expected, IReadOnlyList<IReadOnlyList<string>>? actual) =>
        actual is not null && expected.Count == actual.Count && expected.Zip(actual).All(pair => pair.Second is not null && pair.First.SequenceEqual(pair.Second));

    private static IReadOnlyList<string> Bounded(IReadOnlyList<string> command, long maximumBytes)
    {
        var args = command.ToList();
        var overwrite = args.IndexOf("-y");
        if (overwrite >= 0) args[overwrite] = "-n";
        if (!args.Contains("-nostdin")) args.Insert(0, "-nostdin");
        var output = args[^1];
        args.RemoveAt(args.Count - 1);
        // One byte beyond the accepted size lets a stopped/oversized output be refused,
        // rather than mistaken for a complete measurement at the exact limit.
        args.AddRange(["-fs", (maximumBytes + 1).ToString(CultureInfo.InvariantCulture), output]);
        return args.ToArray();
    }
}
