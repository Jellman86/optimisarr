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
    /// <param name="sourceAudioLanguages">
    /// The source's per-track audio language tags in stream order, or <c>null</c> when they are
    /// unknown (e.g. probed before languages were captured) — in which case the kept-languages
    /// rule stays a no-op rather than guessing.
    /// </param>
    public static TranscodeSpec Resolve(
        RuleSettings rules,
        string inputPath,
        string relativePath,
        string workRoot,
        bool sourceIsHdr,
        int? crf,
        string? preset,
        MediaKind kind = MediaKind.Video,
        bool sourceHasImageSubtitles = false,
        bool sourceHasMp4IncompatibleAudio = false,
        string? sourceImageCodec = null,
        int sourceMaxAudioChannels = 0,
        bool sourceIsVariableFrameRate = false,
        IReadOnlyList<string?>? sourceAudioLanguages = null)
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
                ImageScaleFilter: ImageScale.BuildFilter(rules.ImageDownscaleMode, rules.ImageDownscaleValue),
                ImageLossless: image.Encoder == "libwebp"
                    && sourceImageCodec is not null
                    && ImageTarget.IsLossless(sourceImageCodec));
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
                AudioBitrateKbps: AudioTarget.EffectiveBitrateKbps(
                    rules.AudioBitrateKbps, sourceMaxAudioChannels, rules.DownmixToStereo),
                DownmixToStereo: rules.DownmixToStereo);
        }

        // MP4 can only carry text subtitles (mov_text) and has no tag for some Blu-ray audio formats
        // (TrueHD, LPCM). If the source has image-based subtitles (PGS/VobSub), or audio MP4 can't mux
        // that is being copied rather than re-encoded to a compatible codec, fall back to MKV so the
        // stream survives instead of aborting the encode. Any other target container already holds them.
        var copyingAudio = rules.VideoAudioCodec is null;
        var audioForcesMkv = sourceHasMp4IncompatibleAudio && copyingAudio;
        var container = (sourceHasImageSubtitles || audioForcesMkv) && IsMp4Container(rules.TargetContainer)
            ? "mkv"
            : rules.TargetContainer;
        var outputPath = BuildOutputPath(workRoot, relativePath, container);

        // Tone-map only when re-encoding an HDR source under a library that asks for it.
        var tonemap = sourceIsHdr
            && rules.TargetVideoCodec is not null
            && rules.Hdr == HdrHandling.TonemapToSdr;

        // By default a video's audio is copied untouched; a library can opt into re-encoding
        // it to a chosen codec/bitrate, reusing the same encoder mapping as audio-only jobs.
        var audioEncoder = rules.VideoAudioCodec is { } videoAudioCodec
            ? AudioTarget.Resolve(videoAudioCodec).Encoder
            : null;

        var removedAudio = sourceAudioLanguages is null
            ? Array.Empty<int>()
            : AudioTrackSelection.SelectRemovals(sourceAudioLanguages, rules.KeepAudioLanguages);

        return new TranscodeSpec(
            inputPath,
            outputPath,
            rules.TargetVideoCodec,
            rules.TargetVideoCodec is null ? null : crf,
            rules.TargetVideoCodec is null ? null : preset,
            tonemap,
            AudioEncoder: audioEncoder,
            AudioBitrateKbps: audioEncoder is null
                ? null
                : AudioTarget.EffectiveBitrateKbps(
                    rules.VideoAudioBitrateKbps, sourceMaxAudioChannels, rules.DownmixToStereo),
            SourceIsVariableFrameRate: sourceIsVariableFrameRate,
            // A downmix needs an audio re-encode; a copied track keeps its layout.
            DownmixToStereo: audioEncoder is not null && rules.DownmixToStereo,
            RemoveAudioStreamIndexes: removedAudio.Count > 0 ? removedAudio : null);
    }

    /// <summary>True for MP4-family containers, which cannot store image-based subtitles.</summary>
    public static bool IsMp4Container(string? container) =>
        container?.TrimStart('.').ToLowerInvariant() is "mp4" or "m4v" or "mov";

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
