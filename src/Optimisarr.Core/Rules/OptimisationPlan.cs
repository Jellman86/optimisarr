using Optimisarr.Core.Domain;
using Optimisarr.Core.Queue;

namespace Optimisarr.Core.Rules;

/// <summary>
/// What a re-encode does to the picture. A null size or cap means it is left as it is.
/// <see cref="Width"/> and <see cref="Height"/> are the exact new size, known only when no black
/// bars are to be removed; <see cref="ShrinkToHeight"/> is the limit the size is worked out from.
/// <see cref="FrameRateCap"/> and <see cref="CropBlackBars"/> are rules, not findings: whether
/// they change this file is known only once the job has measured it.
/// </summary>
public sealed record PlannedPicture(
    string Codec,
    int? Crf,
    int? Width,
    int? Height,
    int? ShrinkToHeight,
    int? FrameRateCap,
    bool TonemapToSdr,
    bool Deinterlace,
    bool CropBlackBars,
    NoiseReduction NoiseReduction,
    bool Upscales);

/// <summary>What happens to an image. <see cref="RecompressesLossy"/> is true when the source is
/// already a compressed image, so it loses quality a second time.</summary>
public sealed record PlannedImage(string Format, int Quality, ImageDownscaleMode ResizeMode, int ResizeValue, bool RecompressesLossy);

/// <summary>What happens to the sound. A null codec means every kept track is copied untouched.</summary>
public sealed record PlannedSound(string? Codec, int? BitrateKbps, bool DownmixToStereo);

/// <summary>One audio or subtitle track of the source, in stream order, and whether the new file keeps it.</summary>
public sealed record PlannedTrack(int Index, string? Language, bool Kept);

/// <summary>
/// The file that would be written. Audio that MP4 cannot carry is already known from the
/// inventory and is reflected in the name. <see cref="MayBecomeMatroska"/> is true when an MP4
/// result can still move to Matroska because of picture subtitles, which only the job reads.
/// </summary>
public sealed record PlannedFile(string Container, string FileName, bool Renamed, bool MayBecomeMatroska);

/// <summary>
/// What optimising one file would do, by its library's settings as they are now. Read-only: the
/// same resolver the queue uses, run over the facts the inventory already holds, with no FFmpeg.
/// </summary>
public sealed record OptimisationPlan(
    MediaKind Kind,
    string Action,
    PlannedPicture? Picture,
    PlannedSound? Sound,
    IReadOnlyList<PlannedTrack>? AudioTracks,
    IReadOnlyList<PlannedTrack>? SubtitleTracks,
    PlannedFile File,
    PlannedImage? Image = null);

public static class OptimisationPlanner
{
    public const string Encode = "Encode";
    public const string Remux = "Remux";
    public const string TrackCleanup = "TrackCleanup";
    public const string Audio = "Audio";
    public const string Image = "Image";

    /// <param name="crf">The library's own quality number, or its preset's when it has none.</param>
    /// <param name="sourceHasMp4IncompatibleAudio">The inventory's audio codecs include one MP4 cannot hold.</param>
    /// <param name="upscales">The job would run through AI upscaling, with the rules that run gives it.</param>
    public static OptimisationPlan Plan(
        MediaProperties media,
        RuleSettings rules,
        int? crf,
        bool sourceHasMp4IncompatibleAudio = false,
        bool upscales = false)
    {
        var spec = TranscodeSpecResolver.Resolve(
            rules,
            media.RelativePath,
            media.RelativePath,
            workRoot: string.Empty,
            media.IsHdr,
            crf,
            preset: null,
            media.Kind,
            sourceHasMp4IncompatibleAudio: sourceHasMp4IncompatibleAudio,
            sourceImageCodec: media.VideoCodec,
            sourceMaxAudioChannels: media.MaxAudioChannels,
            sourceAudioLanguages: media.AudioLanguages,
            sourceSubtitleLanguages: media.SubtitleLanguages,
            sourceWidth: media.Width,
            sourceHeight: media.Height);

        var fileName = Path.GetFileName(spec.OutputPath);
        var container = Path.GetExtension(fileName).TrimStart('.');
        var file = new PlannedFile(
            container,
            fileName,
            Renamed: !string.Equals(fileName, Path.GetFileName(media.RelativePath), StringComparison.Ordinal),
            // Only a video written as MP4 can still be moved, and only by picture subtitles: the
            // inventory records how many subtitle tracks there are, not what kind they are.
            MayBecomeMatroska: media.Kind == MediaKind.Video
                && rules.TargetContainer is not null
                && (media.SubtitleTrackCount > 0 || media.SubtitleLanguages is { Count: > 0 })
                && TranscodeSpecResolver.IsMp4Container(container));

        if (media.Kind == MediaKind.Image)
        {
            var image = new PlannedImage(
                rules.TargetImageFormat,
                rules.ImageQuality,
                rules.ImageDownscaleMode,
                rules.ImageDownscaleValue,
                RecompressesLossy: media.VideoCodec is { } imageCodec && !ImageTarget.IsLossless(imageCodec));
            return new OptimisationPlan(media.Kind, Image, null, null, null, null, file, image);
        }

        if (media.Kind == MediaKind.Audio)
        {
            return new OptimisationPlan(
                media.Kind,
                Audio,
                null,
                new PlannedSound(rules.TargetAudioCodec, spec.AudioBitrateKbps, spec.DownmixToStereo),
                null,
                null,
                file);
        }

        var encodes = spec.VideoCodec is not null;
        var picture = encodes
            ? new PlannedPicture(
                spec.VideoCodec!,
                spec.Crf,
                // With bars to remove, the size is worked out from the picture left after the
                // crop, which only the job measures. Before that, only the limit is known.
                rules.CropBlackBars ? null : spec.DownscaleTo?.Width,
                rules.CropBlackBars ? null : spec.DownscaleTo?.Height,
                spec.DownscaleTo is null ? null : rules.VideoDownscaleHeight,
                rules.MaxFrameRate,
                spec.TonemapToSdr,
                VideoFieldOrder.Deinterlaces(rules.DeinterlaceVideo, media.FieldOrder),
                rules.CropBlackBars,
                rules.NoiseReduction,
                upscales)
            : null;

        return new OptimisationPlan(
            media.Kind,
            encodes ? Encode : rules.TargetContainer is null ? TrackCleanup : Remux,
            picture,
            new PlannedSound(spec.AudioEncoder is null ? null : rules.VideoAudioCodec, spec.AudioBitrateKbps, spec.DownmixToStereo),
            Tracks(media.AudioLanguages, spec.RemoveAudioStreamIndexes),
            Tracks(media.SubtitleLanguages, spec.RemoveSubtitleStreamIndexes),
            file);
    }

    // Null when the languages were never captured: the kept-languages rule then removes nothing,
    // and the plan must not pretend to know which tracks there are.
    private static IReadOnlyList<PlannedTrack>? Tracks(IReadOnlyList<string?>? languages, IReadOnlyList<int>? removed) =>
        languages?.Select((language, index) => new PlannedTrack(index, language, removed?.Contains(index) != true)).ToList();
}
