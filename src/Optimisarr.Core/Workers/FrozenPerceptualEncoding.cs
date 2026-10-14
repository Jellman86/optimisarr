using System.Globalization;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Queue;

namespace Optimisarr.Core.Workers;

/// <summary>Claim-time command intent. Settling a search changes one encoder control, never reloads library settings.</summary>
public sealed record FrozenPerceptualEncoding(Guid ContractId, TranscodeSpec Spec, IReadOnlyList<string> Arguments)
{
    public static FrozenPerceptualEncoding Create(RemotePerceptualSearchContract contract, TranscodeSpec spec, IReadOnlyList<string> arguments)
    {
        Validate(contract, spec, arguments);
        return new(contract.Id, spec, Array.AsReadOnly(arguments.ToArray()));
    }

    public FrozenPerceptualEncoding Select(RemotePerceptualSearchState state)
    {
        if (state is null || state.Contract?.Id != ContractId || !state.CanEncode)
            throw new InvalidOperationException("A final encode requires this contract's completed perceptual search.");
        var index = Validate(state.Contract, Spec, Arguments);
        var value = state.Decision!.SelectedValue;
        var selected = Spec.Kind == MediaKind.Image ? Spec with { ImageQuality = value } : Spec with { AudioBitrateKbps = value };
        var args = Arguments.ToArray();
        args[index] = Setting(selected);
        return new(ContractId, selected, Array.AsReadOnly(args));
    }

    private static int Validate(RemotePerceptualSearchContract contract, TranscodeSpec spec, IReadOnlyList<string> args)
    {
        if (spec is null || args is null || args.Count < 2 || args.Any(arg => arg is null) || spec.AudioSampleOnly
            || spec.InputPath != WorkerProtocol.InputPlaceholder || spec.OutputPath != WorkerProtocol.OutputPlaceholder + RemotePerceptualSearchPlanner.OutputExtension(contract.Encoder)
            || args[^1] != spec.OutputPath || args.Count(arg => arg == WorkerProtocol.InputPlaceholder) != 1)
            throw new ArgumentException("A frozen final command requires portable paths, a complete encode and unambiguous source/output arguments.");
        // Reuse the same supported-source/encoder checks as the sample planner. No process is run.
        RemotePerceptualSearchPlanner.Plan(contract, contract.Plan.Baseline, 0, spec);
        if (args[Unique(args, "-i")] != WorkerProtocol.InputPlaceholder)
            throw new ArgumentException("The frozen encode must read only its assigned source.");
        var codec = Unique(args, spec.Kind == MediaKind.Image ? "-c:v" : "-c:a");
        if (args[codec] != contract.Encoder) throw new ArgumentException("The frozen command names another encoder.");
        var index = Unique(args, spec.Kind == MediaKind.Audio ? "-b:a" : contract.Encoder == "mjpeg" ? "-q:v" : "-quality");
        if (args[index] != Setting(spec)) throw new ArgumentException("The frozen command does not match its saved encoder setting.");
        return index;
    }

    private static int Unique(IReadOnlyList<string> args, string option)
    {
        var indexes = Enumerable.Range(0, args.Count).Where(index => args[index] == option).ToArray();
        if (indexes.Length != 1 || indexes[0] + 1 >= args.Count - 1)
            throw new ArgumentException("The frozen encode must have exactly one " + option + " control.");
        return indexes[0] + 1;
    }

    private static string Setting(TranscodeSpec spec) => spec.Kind == MediaKind.Audio
        ? (spec.AudioBitrateKbps ?? AudioTarget.DefaultBitrateKbps).ToString(CultureInfo.InvariantCulture) + "k"
        : (spec.ImageEncoder == "mjpeg" ? FfmpegCommandBuilder.JpegQuantizer(spec.ImageQuality ?? ImageTarget.DefaultQuality)
            : spec.ImageQuality ?? ImageTarget.DefaultQuality).ToString(CultureInfo.InvariantCulture);
}
