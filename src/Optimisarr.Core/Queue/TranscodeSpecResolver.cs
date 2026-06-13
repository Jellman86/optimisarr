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
        string? preset,
        MediaKind kind = MediaKind.Video)
    {
        if (kind == MediaKind.Image)
        {
            var image = ImageTarget.Resolve(rules.TargetImageFormat);
            return new TranscodeSpec(
                inputPath,
                BuildOutputPath(workRoot, relativePath, image.Extension),
                VideoCodec: null,
                Crf: null,
                Preset: null,
                TonemapToSdr: false,
                Kind: MediaKind.Image,
                ImageEncoder: image.Encoder,
                ImageQuality: rules.ImageQuality,
                ImageScaleFilter: ImageScale.BuildFilter(rules.ImageDownscaleMode, rules.ImageDownscaleValue));
        }

        if (kind == MediaKind.Audio)
        {
            var audio = AudioTarget.Resolve(rules.TargetAudioCodec);
            return new TranscodeSpec(
                inputPath,
                BuildOutputPath(workRoot, relativePath, audio.Container),
                VideoCodec: null,
                Crf: null,
                Preset: null,
                TonemapToSdr: false,
                Kind: MediaKind.Audio,
                AudioEncoder: audio.Encoder,
                AudioBitrateKbps: rules.AudioBitrateKbps,
                DownmixToStereo: rules.DownmixToStereo);
        }

        var outputPath = BuildOutputPath(workRoot, relativePath, rules.TargetContainer);

        // Tone-map only when re-encoding an HDR source under a library that asks for it.
        var tonemap = sourceIsHdr
            && rules.TargetVideoCodec is not null
            && rules.Hdr == HdrHandling.TonemapToSdr;

        // By default a video's audio is copied untouched; a library can opt into re-encoding
        // it to a chosen codec/bitrate, reusing the same encoder mapping as audio-only jobs.
        var audioEncoder = rules.VideoAudioCodec is { } videoAudioCodec
            ? AudioTarget.Resolve(videoAudioCodec).Encoder
            : null;

        return new TranscodeSpec(
            inputPath,
            outputPath,
            rules.TargetVideoCodec,
            rules.TargetVideoCodec is null ? null : crf,
            rules.TargetVideoCodec is null ? null : preset,
            tonemap,
            AudioEncoder: audioEncoder,
            AudioBitrateKbps: audioEncoder is null ? null : rules.VideoAudioBitrateKbps,
            // A downmix needs an audio re-encode; a copied track keeps its layout.
            DownmixToStereo: audioEncoder is not null && rules.DownmixToStereo);
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
