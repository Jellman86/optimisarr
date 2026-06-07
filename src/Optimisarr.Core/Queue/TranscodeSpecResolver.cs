using Optimisarr.Core.Domain;
using Optimisarr.Core.Rules;

namespace Optimisarr.Core.Queue;

/// <summary>
/// Turns a library's resolved <see cref="RuleSettings"/> and a media file into a
/// concrete <see cref="TranscodeSpec"/>: where the output goes under the work root,
/// the target codec/container, and whether to tone-map. Pure and unit tested.
/// </summary>
public static class TranscodeSpecResolver
{
    public static TranscodeSpec Resolve(
        RuleSettings rules,
        string inputPath,
        string relativePath,
        string workRoot,
        bool sourceIsHdr,
        int? crf,
        string? preset)
    {
        var outputPath = BuildOutputPath(workRoot, relativePath, rules.TargetContainer);

        // Tone-map only when re-encoding an HDR source under a library that asks for it.
        var tonemap = sourceIsHdr
            && rules.TargetVideoCodec is not null
            && rules.Hdr == HdrHandling.TonemapToSdr;

        return new TranscodeSpec(
            inputPath,
            outputPath,
            rules.TargetVideoCodec,
            rules.TargetVideoCodec is null ? null : crf,
            rules.TargetVideoCodec is null ? null : preset,
            tonemap);
    }

    private static string BuildOutputPath(string workRoot, string relativePath, string targetContainer)
    {
        var directory = Path.GetDirectoryName(relativePath);
        var fileName = $"{Path.GetFileNameWithoutExtension(relativePath)}.{targetContainer.TrimStart('.')}";

        // Use forward slashes so paths are stable across platforms and easy to assert.
        var relativeOutput = string.IsNullOrEmpty(directory)
            ? fileName
            : $"{directory.Replace('\\', '/')}/{fileName}";

        return $"{workRoot.TrimEnd('/')}/{relativeOutput}";
    }
}
